using Microsoft.EntityFrameworkCore;

namespace LifeService.Infrastructure.Persistence;

/// <summary>
/// EF Core persistence model for LifeService. Domain types are mapped to flat entities; the sparse
/// cell set is stored as JSON so the same model works across relational providers (SQLite for dev,
/// SQL Server / PostgreSQL for prod). See docs/persistence.md.
/// </summary>
public sealed class LifeDbContext : DbContext
{
    public LifeDbContext(DbContextOptions<LifeDbContext> options) : base(options)
    {
    }

    public DbSet<StateEntity> States => Set<StateEntity>();
    public DbSet<SummaryEntity> Summaries => Set<SummaryEntity>();
    public DbSet<QuarantineEntity> Quarantines => Set<QuarantineEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StateEntity>(e =>
        {
            e.HasKey(s => new { s.BoardId, s.Label });
            e.Property(s => s.CellsJson).IsRequired();
        });

        modelBuilder.Entity<SummaryEntity>(e =>
        {
            e.HasKey(s => s.BoardId);
            // Unique fingerprint enforces content-addressed (idempotent) board creation at the
            // database level, even under concurrent uploads of the same initial state.
            e.Property(s => s.Fingerprint).IsRequired();
            e.HasIndex(s => s.Fingerprint).IsUnique();
        });

        modelBuilder.Entity<QuarantineEntity>(e =>
        {
            e.HasKey(q => q.BoardId);
            e.Property(q => q.Reason).IsRequired();
        });
    }
}

/// <summary>A single generation, with its live cells serialised as JSON.</summary>
public sealed class StateEntity
{
    public Guid BoardId { get; set; }
    public long Label { get; set; }
    public string CellsJson { get; set; } = "[]";
}

/// <summary>The latest solution summary for a board.</summary>
public sealed class SummaryEntity
{
    public Guid BoardId { get; set; }
    public int Status { get; set; }
    public long LastComputedLabel { get; set; }
    public long? OscillationPeriodStart { get; set; }
    public int? OscillationPeriodLength { get; set; }

    /// <summary>Content fingerprint of the board's initial state (unique; idempotent uploads).</summary>
    public string Fingerprint { get; set; } = string.Empty;
}

/// <summary>Quarantine / failure-tracking record for a board.</summary>
public sealed class QuarantineEntity
{
    public Guid BoardId { get; set; }
    public DateTimeOffset QuarantinedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int RetryCount { get; set; }
}
