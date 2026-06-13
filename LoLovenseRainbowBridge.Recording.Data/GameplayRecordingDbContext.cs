using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LoLovenseRainbowBridge.Recording.Data;

public sealed class GameEntity
{
    public string GameId { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public string AppVersion { get; set; } = "";
    public string ConfigSummaryJson { get; set; } = "";
}

public sealed class LovenseRecordEntity
{
    public long Id { get; set; }
    public string GameId { get; set; } = "";
    public string DateTime { get; set; } = "";
    public long OffsetMs { get; set; }
    public int DurationMs { get; set; }
    public string ContextDiffJson { get; set; } = "";
    public GameEntity? Game { get; set; }
}

public sealed class GameplayRecordingDbContext(DbContextOptions<GameplayRecordingDbContext> options) : DbContext(options)
{
    public DbSet<GameEntity> Games => Set<GameEntity>();
    public DbSet<LovenseRecordEntity> LovenseRecords => Set<LovenseRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameEntity>(game =>
        {
            game.ToTable("games");
            game.HasKey(x => x.GameId);
            game.Property(x => x.GameId).HasColumnName("game_id").IsRequired();
            game.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
            game.Property(x => x.EndedAt).HasColumnName("ended_at");
            game.Property(x => x.AppVersion).HasColumnName("app_version").IsRequired();
            game.Property(x => x.ConfigSummaryJson).HasColumnName("config_summary_json").IsRequired();
        });

        modelBuilder.Entity<LovenseRecordEntity>(record =>
        {
            record.ToTable("lovense_records");
            record.HasKey(x => x.Id);
            record.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            record.Property(x => x.GameId).HasColumnName("game_id").IsRequired();
            record.Property(x => x.DateTime).HasColumnName("datetime").IsRequired();
            record.Property(x => x.OffsetMs).HasColumnName("offset_ms");
            record.Property(x => x.DurationMs).HasColumnName("duration_ms");
            record.Property(x => x.ContextDiffJson).HasColumnName("context_diff_json").IsRequired();
            record.HasOne(x => x.Game).WithMany().HasForeignKey(x => x.GameId);
            record.HasIndex(x => new { x.GameId, x.OffsetMs, x.Id }).HasDatabaseName("ix_lovense_records_game_offset");
        });
    }
}

public sealed class GameplayRecordingDbContextFactory : IDesignTimeDbContextFactory<GameplayRecordingDbContext>
{
    public GameplayRecordingDbContext CreateDbContext(string[] args)
    {
        Directory.CreateDirectory("data");

        var options = new DbContextOptionsBuilder<GameplayRecordingDbContext>()
            .UseSqlite("Data Source=data/gameplay.sqlite")
            .Options;

        return new GameplayRecordingDbContext(options);
    }
}
