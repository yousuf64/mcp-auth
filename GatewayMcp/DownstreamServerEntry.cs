namespace GatewayMcp;

public class DownstreamServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>
    /// Status values: disconnected | connecting | connected | auth_required
    /// </summary>
    public string Status { get; set; } = "disconnected";
}
