using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GatewayMcp;

public class DownstreamMcpRegistry
{
    private const string GatewayCallbackBase = "http://localhost:7071/api/oauth/callback";
    private const string DataFile = "downstream-servers.json";
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, DownstreamServerEntry> _servers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pendingCallbacks = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DownstreamMcpRegistry> _logger;

    public DownstreamMcpRegistry(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DownstreamMcpRegistry>();
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(DataFile))
            return;

        try
        {
            var json = File.ReadAllText(DataFile);
            var entries = JsonSerializer.Deserialize<List<DownstreamServerEntry>>(json, _jsonOptions);
            if (entries is null) return;

            foreach (var entry in entries)
            {
                var cache = new FileTokenCache(entry.Id);
                entry.Status = cache.HasTokens() ? "connected" : "disconnected";
                _servers[entry.Id] = entry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load downstream servers from disk.");
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_servers.Values.ToList(), _jsonOptions);
            File.WriteAllText(DataFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save downstream servers to disk.");
        }
    }

    public IEnumerable<DownstreamServerEntry> GetServers() => _servers.Values;

    public DownstreamServerEntry AddServer(string name, string url)
    {
        var entry = new DownstreamServerEntry { Name = name, Url = url };
        _servers[entry.Id] = entry;
        SaveToDisk();
        return entry;
    }

    public bool RemoveServer(string id)
    {
        if (!_servers.TryRemove(id, out _))
            return false;

        var cache = new FileTokenCache(id);
        cache.DeleteTokens();
        SaveToDisk();
        return true;
    }

    /// <summary>
    /// Initiates the OAuth connect flow for the given server.
    /// Fires a background task that creates an MCP client, triggering OAuth.
    /// The AuthorizationRedirectDelegate signals authUrlTcs with the Keycloak URL.
    /// Returns the Keycloak authorization URL to redirect the browser to.
    /// </summary>
    public async Task<Uri> InitiateConnectAsync(string id, CancellationToken cancellationToken)
    {
        if (!_servers.TryGetValue(id, out var entry))
            throw new KeyNotFoundException($"Server '{id}' not found.");

        entry.Status = "connecting";

        var tokenCache = new FileTokenCache(id);
        var authUrlTcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        var oauthOptions = new ClientOAuthOptions
        {
            RedirectUri = new Uri($"{GatewayCallbackBase}/{entry.Id}"),
            TokenCache = tokenCache,
            AuthorizationRedirectDelegate = async (authorizationUrl, redirectUri, ct) =>
            {
                // Signal the Keycloak authorization URL to the waiting /connect endpoint
                authUrlTcs.TrySetResult(authorizationUrl);

                // Register TCS keyed by server ID — no state parameter needed
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
    /// Called when the browser completes the OAuth flow and Keycloak redirects back.
    /// </summary>
    public bool CompleteOAuthCallback(string code, string serverId)
    {
        if (_pendingCallbacks.TryGetValue(serverId, out var tcs))
        {
            tcs.TrySetResult(code);
            return true;
        }

        _logger.LogWarning("No pending OAuth callback found for server '{ServerId}'.", serverId);
        return false;
    }

    /// <summary>
    /// Relays a tool call to the named downstream MCP server using stored tokens.
    /// </summary>
    public async Task<string> RelayCallAsync(string serverName, string tool, string paramsJson, CancellationToken cancellationToken)
    {
        var entry = _servers.Values.FirstOrDefault(s =>
            string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            return $"Error: No downstream server named '{serverName}' is registered.";

        var tokenCache = new FileTokenCache(entry.Id);
        if (!tokenCache.HasTokens())
            return $"Error: Server '{serverName}' is not connected. Use the dashboard to authorize it first.";

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

        try
        {
            var oauthOptions = new ClientOAuthOptions
            {
                RedirectUri = new Uri($"{GatewayCallbackBase}/{entry.Id}"),
                TokenCache = tokenCache,
                AuthorizationRedirectDelegate = (_, _, _) =>
                {
                    entry.Status = "auth_required";
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
        catch (Exception) when (entry.Status == "auth_required")
        {
            return $"Error: Server '{serverName}' requires re-authorization. Please use the dashboard to reconnect.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relay call to '{ServerName}/{Tool}' failed.", serverName, tool);
            return $"Error calling '{tool}' on '{serverName}': {ex.Message}";
        }
    }
}
