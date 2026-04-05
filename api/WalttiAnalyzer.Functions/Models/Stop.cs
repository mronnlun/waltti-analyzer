namespace WalttiAnalyzer.Functions.Models;

public class Stop
{
    public long Id { get; set; }
    public string GtfsId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public long UpdatedAt { get; set; }
}
