namespace WalttiAnalyzer.Core.Models;

public class Route
{
    public long Id { get; set; }
    public string GtfsId { get; set; } = "";
    public string? ShortName { get; set; }
    public string? LongName { get; set; }
    public string? Mode { get; set; }
    public long UpdatedAt { get; set; }
}
