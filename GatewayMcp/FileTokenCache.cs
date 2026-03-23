using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace GatewayMcp;

public class FileTokenCache : ITokenCache
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public FileTokenCache(string userId, string serverId)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "tokens");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, $"{userId}-{serverId}.json");
    }

    public bool HasTokens() => File.Exists(_filePath);

    /// <summary>
    /// Returns true if a token file exists AND the tokens are not known-expired.
    /// If ObtainedAt or ExpiresIn are absent, defaults to true (cannot determine expiry).
    /// Uses a 60-second buffer before the actual expiry to account for clock skew.
    /// </summary>
    public bool HasUsableTokens()
    {
        if (!File.Exists(_filePath)) return false;

        try
        {
            var json = File.ReadAllText(_filePath);
            var tokens = JsonSerializer.Deserialize<TokenContainer>(json, _jsonOptions);
            if (tokens is null || tokens.ExpiresIn is null || tokens.ObtainedAt == default)
                return true; // Cannot determine expiry — assume usable

            var expiry = tokens.ObtainedAt.AddSeconds(tokens.ExpiresIn.Value - 60);
            return DateTimeOffset.UtcNow < expiry;
        }
        catch
        {
            return true; // Deserialisation failure — let the relay attempt and fail naturally
        }
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return JsonSerializer.Deserialize<TokenContainer>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(tokens, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }

    public void DeleteTokens()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }
}
