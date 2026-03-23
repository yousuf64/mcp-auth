using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GatewayMcp;

public class DownstreamMcpRegistry
{
    private const string GatewayCallbackBase = "http://localhost:7071/api/oauth/callback";
    private const string GatewayBaseUrl = "http://localhost:7071";
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Key: "{userId}:{serverId}" — unique per user-server pair
    private readonly ConcurrentDictionary<string, DownstreamServerEntry> _servers = new();

    // Key: serverId — awaiting Keycloak authorization code from browser flow
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pendingCallbacks = new();

    // Key: elicitationId — awaiting token acquisition so we can notify the MCP client to retry
    private readonly ConcurrentDictionary<string, (string UserId, string ServerId, McpServer Server)> _pendingElicitations = new();

    // Per-process random key for HMAC-SHA256 signed connect tokens
    private readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DownstreamMcpRegistry> _logger;

    public DownstreamMcpRegistry(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DownstreamMcpRegistry>();
        LoadFromDisk();
    }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    private void LoadFromDisk()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "servers-*.json"))
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var userId = fileName.Substring("servers-".Length);
                var json = File.ReadAllText(file);
                var entries = JsonSerializer.Deserialize<List<DownstreamServerEntry>>(json, _jsonOptions);
                if (entries is null) continue;

                foreach (var entry in entries)
                {
                    var cache = new FileTokenCache(userId, entry.Id);
                    entry.Status = cache.HasTokens() ? "connected" : "disconnected";
                    _servers[$"{userId}:{entry.Id}"] = entry;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load downstream servers from '{File}'.", file);
            }
        }
    }

    private void SaveToDisk(string userId)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            var userEntries = _servers
                .Where(kv => kv.Key.StartsWith($"{userId}:"))
                .Select(kv => kv.Value)
                .ToList();
            var json = JsonSerializer.Serialize(userEntries, _jsonOptions);
            File.WriteAllText(Path.Combine(dir, $"servers-{userId}.json"), json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save downstream servers for user '{UserId}'.", userId);
        }
    }

    // -------------------------------------------------------------------------
    // Server management
    // -------------------------------------------------------------------------

    public IEnumerable<DownstreamServerEntry> GetServers(string userId) =>
        _servers
            .Where(kv => kv.Key.StartsWith($"{userId}:"))
            .Select(kv => kv.Value);

    public DownstreamServerEntry AddServer(string userId, string name, string url)
    {
        var entry = new DownstreamServerEntry { Name = name, Url = url };
        _servers[$"{userId}:{entry.Id}"] = entry;
        SaveToDisk(userId);
        return entry;
    }

    public bool RemoveServer(string userId, string id)
    {
        if (!_servers.TryRemove($"{userId}:{id}", out _))
            return false;

        new FileTokenCache(userId, id).DeleteTokens();
        SaveToDisk(userId);
        return true;
    }

    // -------------------------------------------------------------------------
    // HMAC-signed connect tokens (for elicitation URL)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a signed one-time URL for the browser to initiate the OAuth flow
    /// on behalf of an MCP elicitation request.
    /// Payload: "{userId}:{serverId}:{elicitationId}:{expUnix}"
    /// </summary>
    private string CreateConnectUrl(string userId, string serverId, string elicitationId)
    {
        var expUnix = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = $"{userId}:{serverId}:{elicitationId}:{expUnix}";
        var sig = ComputeHmac(payload);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}:{sig}"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{GatewayBaseUrl}/connect/{serverId}?token={Uri.EscapeDataString(token)}&elicitationId={Uri.EscapeDataString(elicitationId)}";
    }

    /// <summary>
    /// Verifies a connect token and extracts userId and elicitationId.
    /// Returns false if the token is invalid, expired, or the serverId doesn't match.
    /// </summary>
    public bool TryVerifyConnectToken(string serverId, string token,
        out string userId, out string elicitationId)
    {
        userId = "";
        elicitationId = "";
        try
        {
            // Restore standard base64 padding
            var base64 = token.Replace('-', '+').Replace('_', '/');
            var rem = base64.Length % 4;
            if (rem != 0) base64 += new string('=', 4 - rem);

            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            // Format: "{userId}:{serverId}:{elicitationId}:{expUnix}:{sig}"
            var lastColon = raw.LastIndexOf(':');
            if (lastColon < 0) return false;

            var payload = raw[..lastColon];
            var providedSig = raw[(lastColon + 1)..];
            if (ComputeHmac(payload) != providedSig) return false;

            // Parse payload
            var parts = payload.Split(':');
            if (parts.Length < 4) return false;

            var payloadUserId = parts[0];
            var payloadServerId = parts[1];
            var payloadElicitationId = parts[2];
            if (!long.TryParse(parts[3], out var expUnix)) return false;

            if (payloadServerId != serverId) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expUnix) return false;

            userId = payloadUserId;
            elicitationId = payloadElicitationId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string ComputeHmac(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(_hmacKey, bytes);
        return Convert.ToHexString(hash);
    }

    // -------------------------------------------------------------------------
    // OAuth connect flows
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initiates the OAuth connect flow for the given server (dashboard popup path).
    /// Returns the Keycloak authorization URL for the browser to open in a popup.
    /// </summary>
    public async Task<Uri> InitiateConnectAsync(string userId, string id, CancellationToken cancellationToken)
    {
        if (!_servers.TryGetValue($"{userId}:{id}", out var entry))
            throw new KeyNotFoundException($"Server '{id}' not found.");

        entry.Status = "connecting";

        var tokenCache = new FileTokenCache(userId, id);
        var authUrlTcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        var oauthOptions = new ClientOAuthOptions
        {
            RedirectUri = new Uri($"{GatewayCallbackBase}/{entry.Id}"),
            TokenCache = tokenCache,
            AuthorizationRedirectDelegate = async (authorizationUrl, redirectUri, ct) =>
            {
                // Signal the Keycloak authorization URL to the waiting /connect endpoint
                authUrlTcs.TrySetResult(authorizationUrl);

                var codeTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingCallbacks[entry.Id] = codeTcs;

                // Wait up to 5 minutes for the browser flow to complete
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);

                try
                {
                    linkedCts.Token.Register(() => codeTcs.TrySetCanceled());
                    return await codeTcs.Task;
                }
                finally
                {
                    _pendingCallbacks.TryRemove(entry.Id, out _);
                }
            }
        };

        // Fire background task — creates a short-lived MCP client to trigger OAuth handshake
        _ = Task.Run(async () =>
        {
            try
            {
                var transportOptions = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(entry.Url),
                    OAuth = oauthOptions
                };

                // Transport owns the HttpClient; do NOT use 'using' on httpClient
                var httpClient = new HttpClient();
                var transport = new HttpClientTransport(transportOptions, httpClient, _loggerFactory);

                // McpClient owns the transport; use await using for the client
                await using var client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);

                entry.Status = "connected";
                _logger.LogInformation("Successfully connected to server '{Name}'.", entry.Name);
            }
            catch (OperationCanceledException)
            {
                if (entry.Status == "connecting")
                    entry.Status = "disconnected";
            }
            catch (Exception ex)
            {
                entry.Status = "disconnected";
                authUrlTcs.TrySetException(ex);
                _logger.LogError(ex, "OAuth connect failed for server '{Name}'.", entry.Name);
            }
        }, cancellationToken);

        // Wait up to 30 seconds for the auth URL (DCR + metadata discovery)
        using var discoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        discoveryCts.Token.Register(() => authUrlTcs.TrySetCanceled());

        return await authUrlTcs.Task;
    }

    /// <summary>
    /// Initiates the OAuth connect flow triggered from an elicitation URL.
    /// Unlike InitiateConnectAsync, this returns the Keycloak auth URL directly
    /// so the caller can redirect the browser straight to Keycloak (no popup needed).
    /// </summary>
    public async Task<Uri> InitiateConnectFromElicitationAsync(string userId, string serverId, CancellationToken cancellationToken)
    {
        if (!_servers.TryGetValue($"{userId}:{serverId}", out var entry))
            throw new KeyNotFoundException($"Server '{serverId}' not found.");

        entry.Status = "connecting";

        var tokenCache = new FileTokenCache(userId, serverId);
        var authUrlTcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        var oauthOptions = new ClientOAuthOptions
        {
            RedirectUri = new Uri($"{GatewayCallbackBase}/{entry.Id}"),
            TokenCache = tokenCache,
            AuthorizationRedirectDelegate = async (authorizationUrl, redirectUri, ct) =>
            {
                // Signal Keycloak auth URL to the /connect endpoint so it can redirect the browser
                authUrlTcs.TrySetResult(authorizationUrl);

                var codeTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingCallbacks[entry.Id] = codeTcs;

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);

                try
                {
                    linkedCts.Token.Register(() => codeTcs.TrySetCanceled());
                    return await codeTcs.Task;
                }
                finally
                {
                    _pendingCallbacks.TryRemove(entry.Id, out _);
                }
            }
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var transportOptions = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(entry.Url),
                    OAuth = oauthOptions
                };

                var httpClient = new HttpClient();
                var transport = new HttpClientTransport(transportOptions, httpClient, _loggerFactory);

                await using var client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);

                entry.Status = "connected";
                _logger.LogInformation("Successfully connected (via elicitation) to server '{Name}'.", entry.Name);
            }
            catch (OperationCanceledException)
            {
                if (entry.Status == "connecting")
                    entry.Status = "disconnected";
            }
            catch (Exception ex)
            {
                entry.Status = "disconnected";
                authUrlTcs.TrySetException(ex);
                _logger.LogError(ex, "OAuth connect (via elicitation) failed for server '{Name}'.", entry.Name);
            }
        }, cancellationToken);

        using var discoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        discoveryCts.Token.Register(() => authUrlTcs.TrySetCanceled());

        return await authUrlTcs.Task;
    }

    /// <summary>
    /// Called when the browser completes the OAuth flow and Keycloak redirects back.
    /// Signals the waiting background task with the authorization code, then fires
    /// notifications/elicitation/complete on any pending MCP elicitations for this server.
    /// </summary>
    public bool CompleteOAuthCallback(string code, string serverId)
    {
        if (!_pendingCallbacks.TryGetValue(serverId, out var tcs))
        {
            _logger.LogWarning("No pending OAuth callback found for server '{ServerId}'.", serverId);
            return false;
        }

        tcs.TrySetResult(code);

        // Notify any MCP clients waiting on elicitations for this server so they auto-retry
        var matchingElicitations = _pendingElicitations
            .Where(kv => kv.Value.ServerId == serverId)
            .ToList();

        foreach (var (elicitationId, pending) in matchingElicitations)
        {
            _pendingElicitations.TryRemove(elicitationId, out _);

            _ = Task.Run(async () =>
            {
                try
                {
                    await pending.Server.SendNotificationAsync(
                        NotificationMethods.ElicitationCompleteNotification,
                        new ElicitationCompleteNotificationParams { ElicitationId = elicitationId },
                        JsonSerializerOptions.Default,
                        CancellationToken.None);

                    _logger.LogInformation(
                        "Sent elicitation/complete for elicitationId='{ElicitationId}' server='{ServerId}'.",
                        elicitationId, serverId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to send elicitation/complete for elicitationId='{ElicitationId}'.", elicitationId);
                }
            });
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // Relay
    // -------------------------------------------------------------------------

    /// <summary>
    /// Relays a tool call to the named downstream MCP server using stored tokens for the given user.
    /// Throws UrlElicitationRequiredException if authorization is needed.
    /// </summary>
    public async Task<string> RelayCallAsync(
        string userId, string serverName, string tool, string paramsJson,
        McpServer mcpServer, CancellationToken cancellationToken)
    {
        var entry = _servers
            .Where(kv => kv.Key.StartsWith($"{userId}:"))
            .Select(kv => kv.Value)
            .FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            return $"Error: No downstream server named '{serverName}' is registered.";

        var tokenCache = new FileTokenCache(userId, entry.Id);
        if (!tokenCache.HasUsableTokens())
        {
            var elicitationId = Guid.NewGuid().ToString("N");
            var connectUrl = CreateConnectUrl(userId, entry.Id, elicitationId);
            _pendingElicitations[elicitationId] = (userId, entry.Id, mcpServer);

            throw new UrlElicitationRequiredException(
                $"Server '{serverName}' requires authorization. Please open the link to connect.",
                [new ElicitRequestParams
                {
                    ElicitationId = elicitationId,
                    Url = connectUrl,
                    Message = $"Authorize access to '{serverName}' to continue.",
                    Mode = "url"
                }]);
        }

        Dictionary<string, object?>? parameters = null;
        if (!string.IsNullOrWhiteSpace(paramsJson) && paramsJson != "{}")
        {
            try
            {
                parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsJson, _jsonOptions);
            }
            catch (Exception ex)
            {
                return $"Error: Invalid paramsJson — {ex.Message}";
            }
        }

        bool authRequired = false;

        try
        {
            var oauthOptions = new ClientOAuthOptions
            {
                RedirectUri = new Uri($"{GatewayCallbackBase}/{entry.Id}"),
                TokenCache = tokenCache,
                AuthorizationRedirectDelegate = (_, _, _) =>
                {
                    authRequired = true;
                    return Task.FromResult<string?>(null);
                }
            };

            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(entry.Url),
                OAuth = oauthOptions
            };

            // Transport owns httpClient; do NOT use 'using' on httpClient
            var httpClient = new HttpClient();
            var transport = new HttpClientTransport(transportOptions, httpClient, _loggerFactory);

            // McpClient owns transport; use await using for client
            await using var client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);

            var result = await client.CallToolAsync(tool, parameters, cancellationToken: cancellationToken);

            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            return string.IsNullOrEmpty(text) ? "(no text content returned)" : text;
        }
        catch (Exception) when (authRequired)
        {
            // Token file exists but proved invalid/expired at use time — trigger fresh elicitation
            var elicitationId = Guid.NewGuid().ToString("N");
            var connectUrl = CreateConnectUrl(userId, entry.Id, elicitationId);
            _pendingElicitations[elicitationId] = (userId, entry.Id, mcpServer);

            throw new UrlElicitationRequiredException(
                $"Server '{serverName}' requires re-authorization. Please open the link to reconnect.",
                [new ElicitRequestParams
                {
                    ElicitationId = elicitationId,
                    Url = connectUrl,
                    Message = $"Re-authorize access to '{serverName}' to continue.",
                    Mode = "url"
                }]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relay call to '{ServerName}/{Tool}' failed.", serverName, tool);
            return $"Error calling '{tool}' on '{serverName}': {ex.Message}";
        }
    }
}
