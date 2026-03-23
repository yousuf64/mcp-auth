using System.Security.Claims;
using System.Web;
using GatewayMcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder();

var serverUrl = "http://localhost:7071/";
var keycloakUrl = "http://localhost:8080/realms/mcp";

builder.Services.AddAuthentication(options =>
    {
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        // In .NET 8+, JwtBearerOptions.MapInboundClaims controls the new JsonWebTokenHandler.
        // Setting false preserves original claim names (sub, preferred_username, etc.)
        options.MapInboundClaims = false;
        options.Authority = keycloakUrl;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAudience = serverUrl,
            ValidIssuer = keycloakUrl,
            NameClaimType = "name",
            RoleClaimType = "roles"
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var name = context.Principal?.Identity?.Name ?? "unknown";
                var email = context.Principal?.FindFirstValue("preferred_username") ?? "unknown";
                Console.WriteLine($"Token validated for: {name} ({email})");
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                Console.WriteLine($"Authentication failed: {context.Exception.Message}");
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                Console.WriteLine("Challenging client to authenticate with Keycloak");
                return Task.CompletedTask;
            }
        };
    })
    .AddMcp(options =>
    {
        options.ResourceMetadata = new()
        {
            ResourceDocumentation = "https://docs.example.com/api/weather",
            AuthorizationServers = { keycloakUrl },
            ScopesSupported = ["mcp:tools"]
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<DownstreamMcpRegistry>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<Tools>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Helper: extract the 'sub' claim from the authenticated user.
static string GetUserId(HttpContext ctx) =>
    ctx.User.FindFirstValue("sub") ?? throw new InvalidOperationException("No sub claim in token.");

// --- REST API ---

// Redirect /dashboard → /index.html, preserving query string (needed for OAuth ?code= callback)
app.MapGet("/dashboard", (HttpContext ctx) =>
{
    var qs = ctx.Request.QueryString.Value;
    return Results.Redirect($"/index.html{qs}");
});

// GET /api/servers
app.MapGet("/api/servers", (DownstreamMcpRegistry registry, HttpContext ctx) =>
    Results.Ok(registry.GetServers(GetUserId(ctx))))
    .RequireAuthorization();

// POST /api/servers
app.MapPost("/api/servers", (DownstreamMcpRegistry registry, HttpContext ctx, AddServerRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest("name and url are required.");

    var entry = registry.AddServer(GetUserId(ctx), req.Name, req.Url);
    return Results.Created($"/api/servers/{entry.Id}", entry);
}).RequireAuthorization();

// DELETE /api/servers/{id}
app.MapDelete("/api/servers/{id}", (DownstreamMcpRegistry registry, HttpContext ctx, string id) =>
    registry.RemoveServer(GetUserId(ctx), id) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization();

// GET /api/servers/{id}/connect — returns JSON { authUrl } so the dashboard can window.open() it
app.MapGet("/api/servers/{id}/connect", async (DownstreamMcpRegistry registry, HttpContext ctx, string id, CancellationToken ct) =>
{
    try
    {
        var authUrl = await registry.InitiateConnectAsync(GetUserId(ctx), id, ct);
        return Results.Ok(new { authUrl = authUrl.ToString() });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(ex.Message);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to initiate connect: {ex.Message}");
    }
}).RequireAuthorization();

// GET /api/oauth/callback/{serverId} — Keycloak redirects here with ?code=...
// Intentionally unauthenticated — this is a browser redirect target, no Bearer token present.
app.MapGet("/api/oauth/callback/{serverId}", (DownstreamMcpRegistry registry, HttpContext ctx, string serverId) =>
{
    var query = HttpUtility.ParseQueryString(ctx.Request.QueryString.Value ?? "");
    var code = query["code"];
    var error = query["error"];

    const string html = """
        <html><head><title>Authorization Complete</title></head>
        <body style="font-family:sans-serif;text-align:center;padding:3rem">
        <h2>Authorization complete</h2>
        <p>You can close this tab and return to the dashboard.</p>
        <script>setTimeout(()=>window.close(),2000)</script>
        </body></html>
        """;

    const string errorHtml = """
        <html><head><title>Authorization Failed</title></head>
        <body style="font-family:sans-serif;text-align:center;padding:3rem">
        <h2>Authorization failed</h2>
        <p>{0}</p>
        </body></html>
        """;

    if (!string.IsNullOrEmpty(error))
        return Results.Content(string.Format(errorHtml, error), "text/html");

    if (string.IsNullOrEmpty(code))
        return Results.Content(string.Format(errorHtml, "Missing code parameter."), "text/html");

    registry.CompleteOAuthCallback(code, serverId);
    return Results.Content(html, "text/html");
});

// GET /connect/{serverId}?token=...&elicitationId=...
// Browser redirect target from an MCP elicitation URL — intentionally unauthenticated.
// Verifies the HMAC-signed token, then redirects the browser directly to Keycloak.
app.MapGet("/connect/{serverId}", async (DownstreamMcpRegistry registry, string serverId,
    string token, string elicitationId, CancellationToken ct) =>
{
    if (!registry.TryVerifyConnectToken(serverId, token, out var userId, out _))
        return Results.BadRequest("Invalid or expired authorization link.");

    try
    {
        var authUrl = await registry.InitiateConnectFromElicitationAsync(userId, serverId, ct);
        return Results.Redirect(authUrl.ToString());
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound("Server not found.");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to initiate authorization: {ex.Message}");
    }
});

// --- MCP ---
app.MapMcp().RequireAuthorization();

Console.WriteLine($"Starting MCP server with authorization at {serverUrl}");
Console.WriteLine($"Using Keycloak OAuth server at {keycloakUrl}");
Console.WriteLine($"Protected Resource Metadata URL: {serverUrl}.well-known/oauth-protected-resource");
Console.WriteLine($"Dashboard: {serverUrl}dashboard");
Console.WriteLine("Press Ctrl+C to stop the server");

await app.RunAsync(serverUrl);

record AddServerRequest(string Name, string Url);
