namespace WalttiAnalyzer.Core.Models;

public class CollectionLogEntry
{
    public long Id { get; set; }
    public long QueriedAt { get; set; }
    public string StopGtfsId { get; set; } = "";
    public string QueryType { get; set; } = "";
    public string? ServiceDate { get; set; }
    public int DeparturesFound { get; set; }
    public int NoService { get; set; }
    public string? Error { get; set; }
}
