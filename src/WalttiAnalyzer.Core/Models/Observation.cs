namespace WalttiAnalyzer.Core.Models;

/// <summary>Denormalized read model returned by observation queries (joins stops, trips, realtime_states).</summary>
public class Observation
{
    public long Id { get; set; }
    public string StopGtfsId { get; set; } = "";
    public string TripGtfsId { get; set; } = "";
    public string ServiceDate { get; set; } = "";
    public int? ScheduledArrival { get; set; }
    public int ScheduledDeparture { get; set; }
    public int? RealtimeArrival { get; set; }
    public int? RealtimeDeparture { get; set; }
    public int? ArrivalDelay { get; set; }
    public int? DepartureDelay { get; set; }
    public int Realtime { get; set; }
    public string? RealtimeState { get; set; }
    public long QueriedAt { get; set; }
    public string? RouteShortName { get; set; }
    public string? RouteLongName { get; set; }
    public string? Mode { get; set; }
    public string? Headsign { get; set; }
    public int? DirectionId { get; set; }
    public string? StopName { get; set; }
}
