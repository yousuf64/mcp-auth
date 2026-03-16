using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

const string ClientBaseUrl = "http://localhost:1182";
const string McpServerUrl = "http://localhost:7072/";
const string CimdPath = "/client-metadata/discord-client.json";
const string CallbackUrl = "http://localhost:1183/callback";
const string ClientMetadataDocumentUrl = ClientBaseUrl + CimdPath;

// Build the CIMD JSON document
var cimdDocument = new
{
    client_id = ClientMetadataDocumentUrl,
    client_name = "DiscordMcpClient",
    redirect_uris = new[] { CallbackUrl },
    grant_types = new[] { "authorization_code" },
    response_types = new[] { "code" },
    token_endpoint_auth_method = "none",
    scope = "discord:tools"
};

var cimdJson = JsonSerializer.Serialize(cimdDocument, new JsonSerializerOptions { WriteIndented = true });

// Start ASP.NET Core mini-server on :1182 to serve the CIMD document
var webApp = WebApplication.CreateBuilder().Build();
webApp.MapGet(CimdPath, () => Results.Content(cimdJson, "application/json"));
webApp.RunAsync(ClientBaseUrl);
Console.WriteLine($"CIMD document server started at {ClientMetadataDocumentUrl}");

// Set up the MCP client with OAuth + CIMD
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

var sharedHandler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
};
var httpClient = new HttpClient(sharedHandler);

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri(McpServerUrl),
    OAuth = new ClientOAuthOptions
    {
        RedirectUri = new Uri(CallbackUrl),
        AuthorizationRedirectDelegate = HandleAuthorizationUrlAsync,
        ClientMetadataDocumentUri = new Uri(ClientMetadataDocumentUrl)
    }
}, httpClient, loggerFactory);

Console.WriteLine($"\nConnecting to Discord MCP server at {McpServerUrl}...");
Console.WriteLine("This will trigger OAuth login via the Discord identity server.");

var mcpClient = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory);

Console.WriteLine("Connected!\n");

// List all tools
Console.WriteLine("=== Available Tools ===");
var tools = await mcpClient.ListToolsAsync();
foreach (var tool in tools)
{
    Console.WriteLine($"  - {tool.Name}: {tool.Description}");
}
Console.WriteLine();

// Call ListChannels
Console.WriteLine("=== Calling ListChannels() ===");
var channelsResult = await mcpClient.CallToolAsync("list_channels", new Dictionary<string, object?>());
Console.WriteLine($"  Result:\n{((TextContentBlock)channelsResult.Content[0]).Text}");

// Call SendMessage
Console.WriteLine("=== Calling SendMessage(channelId: \"general\", text: \"Hello from MCP!\") ===");
var sendResult = await mcpClient.CallToolAsync("send_message", new Dictionary<string, object?>
{
    ["channelId"] = "general",
    ["text"] = "Hello from MCP!"
});
Console.WriteLine($"  Result: {((TextContentBlock)sendResult.Content[0]).Text}");

// Call GetGuildInfo
Console.WriteLine("=== Calling GetGuildInfo() ===");
var guildResult = await mcpClient.CallToolAsync("get_guild_info", new Dictionary<string, object?>());
Console.WriteLine($"  Result:\n{((TextContentBlock)guildResult.Content[0]).Text}");

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();

await webApp.StopAsync();

static async Task<string?> HandleAuthorizationUrlAsync(Uri authorizationUrl, Uri redirectUri, CancellationToken cancellationToken)
{
    Console.WriteLine("Starting OAuth authorization flow...");
    Console.WriteLine($"Opening browser to: {authorizationUrl}");

    // HttpListener prefix must be the authority (scheme+host+port), not the full path
    var listenerPrefix = redirectUri.GetLeftPart(UriPartial.Authority);
    if (!listenerPrefix.EndsWith("/")) listenerPrefix += "/";

    using var listener = new HttpListener();
    listener.Prefixes.Add(listenerPrefix);

    try
    {
        listener.Start();
        Console.WriteLine($"Listening for OAuth callback on: {listenerPrefix}");

        OpenBrowser(authorizationUrl);

        // Wait up to 5 minutes for the user to complete login in the browser.
        // Use a dedicated timeout (not the SDK's cancellationToken which may be short-lived).
        using var loginTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = await listener.GetContextAsync().WaitAsync(loginTimeout.Token);
        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
        var code = query["code"];
        var error = query["error"];

        // Send success page back to browser
        var html = "<html><body><h1>Authentication complete</h1><p>You can close this window now.</p></body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.Close();

        if (!string.IsNullOrEmpty(error))
        {
            Console.WriteLine($"Auth error: {error}");
            return null;
        }

        if (string.IsNullOrEmpty(code))
        {
            Console.WriteLine("No authorization code received.");
            return null;
        }

        Console.WriteLine("Authorization code received successfully.");
        return code;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error getting auth code: {ex.Message}");
        return null;
    }
    finally
    {
        if (listener.IsListening) listener.Stop();
    }
}

static void OpenBrowser(Uri url)
{
    if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
    {
        Console.WriteLine("Error: Only HTTP and HTTPS URLs are allowed.");
        return;
    }

    try
    {
        Process.Start(new ProcessStartInfo { FileName = url.ToString(), UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error opening browser: {ex.Message}");
        Console.WriteLine($"Please manually open this URL: {url}");
    }
}
