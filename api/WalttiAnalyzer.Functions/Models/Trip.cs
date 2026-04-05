namespace WalttiAnalyzer.Functions.Models;

public class Trip
{
    public long Id { get; set; }
    public string GtfsId { get; set; } = "";
    public string? RouteShortName { get; set; }
    public string? RouteLongName { get; set; }
    public string? Mode { get; set; }
    public string? Headsign { get; set; }
    public int? DirectionId { get; set; }
    public long UpdatedAt { get; set; }
}
