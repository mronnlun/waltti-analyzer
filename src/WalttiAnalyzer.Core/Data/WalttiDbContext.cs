using Microsoft.EntityFrameworkCore;
using WalttiAnalyzer.Core.Models;

namespace WalttiAnalyzer.Core.Data;

public class WalttiDbContext : DbContext
{
    public WalttiDbContext(DbContextOptions<WalttiDbContext> options) : base(options) { }

    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<RealtimeState> RealtimeStates => Set<RealtimeState>();
    public DbSet<ObservationRecord> Observations => Set<ObservationRecord>();
    public DbSet<CollectionLogEntry> CollectionLog => Set<CollectionLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Stop>(e =>
        {
            e.ToTable("stops");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.GtfsId).HasColumnName("gtfs_id").IsRequired();
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Lat).HasColumnName("lat");
            e.Property(x => x.Lon).HasColumnName("lon");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.GtfsId).IsUnique();
        });

        modelBuilder.Entity<Route>(e =>
        {
            e.ToTable("routes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.GtfsId).HasColumnName("gtfs_id").IsRequired();
            e.Property(x => x.ShortName).HasColumnName("short_name");
            e.Property(x => x.LongName).HasColumnName("long_name");
            e.Property(x => x.Mode).HasColumnName("mode");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.GtfsId).IsUnique();
        });

        modelBuilder.Entity<Trip>(e =>
        {
            e.ToTable("trips");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.GtfsId).HasColumnName("gtfs_id").IsRequired();
            e.Property(x => x.RouteId).HasColumnName("route_id");
            e.Property(x => x.Headsign).HasColumnName("headsign");
            e.Property(x => x.DirectionId).HasColumnName("direction_id");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.GtfsId).IsUnique();
            e.HasOne(x => x.Route).WithMany().HasForeignKey(x => x.RouteId);
        });

        modelBuilder.Entity<RealtimeState>(e =>
        {
            e.ToTable("realtime_states");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.HasData(
                new RealtimeState { Id = 0, Name = "SCHEDULED" },
                new RealtimeState { Id = 1, Name = "UPDATED" },
                new RealtimeState { Id = 2, Name = "CANCELED" },
                new RealtimeState { Id = 3, Name = "SKIPPED" },
                new RealtimeState { Id = 4, Name = "ADDED" },
                new RealtimeState { Id = 5, Name = "MODIFIED" }
            );
        });

        modelBuilder.Entity<ObservationRecord>(e =>
        {
            e.ToTable("observations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.StopId).HasColumnName("stop_id");
            e.Property(x => x.TripId).HasColumnName("trip_id");
            e.Property(x => x.ServiceDate).HasColumnName("service_date");
            e.Property(x => x.ScheduledDeparture).HasColumnName("scheduled_departure");
            e.Property(x => x.DepartureDelay).HasColumnName("departure_delay");
            e.Property(x => x.DelaySource).HasColumnName("delay_source").HasDefaultValue(0);
            e.Property(x => x.RealtimeStateId).HasColumnName("realtime_state_id");
            e.HasIndex(x => new { x.StopId, x.TripId, x.ServiceDate }).IsUnique().HasDatabaseName("uq_obs_stop_trip_date");
            e.HasIndex(x => new { x.StopId, x.ServiceDate }).HasDatabaseName("idx_obs_stop_date");
            e.HasOne(x => x.Stop).WithMany().HasForeignKey(x => x.StopId);
            e.HasOne(x => x.Trip).WithMany().HasForeignKey(x => x.TripId);
            e.HasOne(x => x.RealtimeStateEntity).WithMany().HasForeignKey(x => x.RealtimeStateId);
        });

        modelBuilder.Entity<CollectionLogEntry>(e =>
        {
            e.ToTable("collection_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.QueriedAt).HasColumnName("queried_at");
            e.Property(x => x.StopGtfsId).HasColumnName("stop_gtfs_id").IsRequired();
            e.Property(x => x.QueryType).HasColumnName("query_type").IsRequired();
            e.Property(x => x.ServiceDate).HasColumnName("service_date");
            e.Property(x => x.DeparturesFound).HasColumnName("departures_found");
            e.Property(x => x.NoService).HasColumnName("no_service").HasDefaultValue(0);
            e.Property(x => x.Error).HasColumnName("error");
        });
    }
}
