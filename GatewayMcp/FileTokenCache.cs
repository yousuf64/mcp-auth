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
