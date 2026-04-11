namespace WalttiAnalyzer.Core.Models;

/// <summary>EF Core entity that maps directly to the observations table.</summary>
public class ObservationRecord
{
    public long Id { get; set; }
    public long StopId { get; set; }
    public long TripId { get; set; }
    public int ServiceDate { get; set; }        // YYYYMMDD integer
    public int ScheduledDeparture { get; set; }  // seconds since midnight
    public int? DepartureDelay { get; set; }     // seconds (positive=late, negative=early)
    public int DelaySource { get; set; }         // 0=SCHEDULED, 1=PROPAGATED, 2=MEASURED
    public int? RealtimeStateId { get; set; }

    public Stop? Stop { get; set; }
    public Trip? Trip { get; set; }
    public RealtimeState? RealtimeStateEntity { get; set; }
}
