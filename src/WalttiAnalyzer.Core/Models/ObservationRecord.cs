namespace WalttiAnalyzer.Core.Models;

/// <summary>EF Core entity that maps directly to the observations table.</summary>
public class ObservationRecord
{
    public long Id { get; set; }
    public long StopId { get; set; }
    public long TripId { get; set; }
    public string ServiceDate { get; set; } = "";
    public int? ScheduledArrival { get; set; }
    public int ScheduledDeparture { get; set; }
    public int? RealtimeArrival { get; set; }
    public int? RealtimeDeparture { get; set; }
    public int? ArrivalDelay { get; set; }
    public int? DepartureDelay { get; set; }
    public int Realtime { get; set; }
    public int? RealtimeStateId { get; set; }
    public long QueriedAt { get; set; }

    public Stop? Stop { get; set; }
    public Trip? Trip { get; set; }
    public RealtimeState? RealtimeStateEntity { get; set; }
}
