using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace DiscordMcp;

[McpServerToolType]
public class Tools
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool, Description("Sends a message to the specified Discord channel.")]
    public static string SendMessage(string channelId, string text) =>
        $"Message sent to #{channelId}: \"{text}\"";

    [McpServerTool, Description("Returns a list of channels in the Discord guild.")]
    public static string ListChannels() =>
        JsonSerializer.Serialize(new[]
        {
            new { id = "111111111111111111", name = "general",    type = "text",  topic = "General discussion" },
            new { id = "222222222222222222", name = "announcements", type = "text", topic = "Official announcements" },
            new { id = "333333333333333333", name = "dev-chat",   type = "text",  topic = "Engineering talk" },
            new { id = "444444444444444444", name = "voice-lobby",type = "voice", topic = "" },
            new { id = "555555555555555555", name = "off-topic",  type = "text",  topic = "Anything goes" }
        }, JsonOpts);

    [McpServerTool, Description("Returns metadata about the Discord guild (server).")]
    public static string GetGuildInfo() =>
        JsonSerializer.Serialize(new
        {
            id = "999999999999999999",
            name = "MCP Dev Guild",
            description = "A guild for MCP developers and enthusiasts.",
            memberCount = 1337,
            onlineCount = 42,
            region = "us-east",
            createdAt = "2023-01-15T10:00:00Z",
            features = new[] { "COMMUNITY", "THREADS_ENABLED", "NEWS" }
        }, JsonOpts);

    [McpServerTool, Description("Returns a list of currently online users in the guild.")]
    public static string GetOnlineUsers() =>
        JsonSerializer.Serialize(new[]
        {
            new { id = "100000000000000001", username = "alice",   discriminator = "0001", status = "online",  activity = (string?)"Writing code" },
            new { id = "100000000000000002", username = "bob",     discriminator = "0002", status = "idle",    activity = (string?)"Listening to Spotify" },
            new { id = "100000000000000003", username = "charlie", discriminator = "0003", status = "online",  activity = (string?)null },
            new { id = "100000000000000004", username = "diana",   discriminator = "0004", status = "dnd",     activity = (string?)"In a meeting" },
            new { id = "100000000000000005", username = "eve",     discriminator = "0005", status = "online",  activity = (string?)"Playing a game" }
        }, JsonOpts);

    [McpServerTool, Description("Creates a new channel in the Discord guild with the given name and type (text or voice).")]
    public static string CreateChannel(string name, string type = "text")
    {
        var validTypes = new[] { "text", "voice", "announcement", "forum" };
        if (!validTypes.Contains(type.ToLowerInvariant()))
            return $"Error: invalid channel type '{type}'. Valid types: {string.Join(", ", validTypes)}";

        var newId = Random.Shared.NextInt64(100_000_000_000_000_000L, 999_999_999_999_999_999L);
        return JsonSerializer.Serialize(new
        {
            id = newId.ToString(),
            name = name.ToLowerInvariant().Replace(' ', '-'),
            type = type.ToLowerInvariant(),
            createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            message = $"Channel #{name} ({type}) created successfully."
        }, JsonOpts);
    }
}
