using System.ComponentModel;
using ModelContextProtocol.Server;

namespace GatewayMcp;

[McpServerToolType]
public class Tools
{
    [McpServerTool, Description("Returns a greeting message for the given name.")]
    public static string SayHello(string name) =>
        $"Hello, {name}! Welcome to the Gateway MCP server.";

    [McpServerTool, Description("Adds two integers and returns the result.")]
    public static int Add(int a, int b) => a + b;

    [McpServerTool, Description("Returns the current UTC date and time on the server.")]
    public static string GetCurrentTime() =>
        $"Current UTC time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";

    [McpServerTool, Description("Echoes back the provided message, optionally in uppercase.")]
    public static string Echo(string message, bool uppercase = false) =>
        uppercase ? message.ToUpperInvariant() : message;

    [McpServerTool, Description("Returns basic info about the server.")]
    public static object GetServerInfo() => new
    {
        Name = "GatewayMcp",
        Version = "1.0.0",
        Description = "An OAuth2/JWT-protected MCP gateway server.",
        UtcNow = DateTime.UtcNow
    };
}