namespace WalttiAnalyzer.Core.Models;

public class Trip
{
    public long Id { get; set; }
    public string GtfsId { get; set; } = "";
    public long RouteId { get; set; }
    public string? Headsign { get; set; }
    public int? DirectionId { get; set; }
    public long UpdatedAt { get; set; }

    public Route? Route { get; set; }
}
