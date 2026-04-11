namespace WalttiAnalyzer.Core.Models;

/// <summary>Denormalized read model returned by observation queries (joins stops, trips, routes, realtime_states).</summary>
public class Observation
{
    public long Id { get; set; }
    public string StopGtfsId { get; set; } = "";
    public string TripGtfsId { get; set; } = "";
    public int ServiceDate { get; set; }          // YYYYMMDD integer
    public int ScheduledDeparture { get; set; }
    public int? DepartureDelay { get; set; }
    public int DelaySource { get; set; }           // 0=SCHEDULED, 1=PROPAGATED, 2=MEASURED
    public string? RealtimeState { get; set; }
    public string? RouteShortName { get; set; }
    public string? Headsign { get; set; }
    public int? DirectionId { get; set; }
    public string? StopName { get; set; }
}
