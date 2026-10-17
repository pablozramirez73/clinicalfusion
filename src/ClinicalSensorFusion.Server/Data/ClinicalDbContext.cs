using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Data;

public sealed class ClinicalDbContext(DbContextOptions<ClinicalDbContext> options) : DbContext(options)
{
    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    public DbSet<SensorChunkEntity> SensorChunks => Set<SensorChunkEntity>();

    public DbSet<AudioRecordingEntity> AudioRecordings => Set<AudioRecordingEntity>();

    public DbSet<AiAnalysisEntity> AiAnalyses => Set<AiAnalysisEntity>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    public DbSet<DashboardAccount> Accounts => Set<DashboardAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var b = modelBuilder;
        b.Entity<Patient>(e =>
        {
            e.ToTable("patients");
            e.Property(p => p.Code).HasMaxLength(64);
            e.HasIndex(p => p.Code).IsUnique();
            e.Property(p => p.FirstName).HasMaxLength(100);
            e.Property(p => p.LastName).HasMaxLength(100);
            e.Property(p => p.FiscalCode).HasMaxLength(16);
            e.HasIndex(p => p.FiscalCode).IsUnique().HasFilter("\"FiscalCode\" IS NOT NULL");
            e.Property(p => p.Phone).HasMaxLength(32);
            e.Property(p => p.Email).HasMaxLength(254);
            e.Property(p => p.Address).HasMaxLength(300);
            e.Property(p => p.Diagnosis).HasMaxLength(2000);
            e.Property(p => p.Notes).HasMaxLength(4000);
            e.Property(p => p.Sex).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.Version).IsRowVersion();
            e.HasIndex(p => new { p.LastName, p.FirstName });
        });

        b.Entity<SessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.Property(s => s.Id).HasMaxLength(64);
            e.Property(s => s.SubjectCode).HasMaxLength(64);
            e.Property(s => s.OperatorId).HasMaxLength(64);
            e.Property(s => s.OperatorName).HasMaxLength(200);
            e.Property(s => s.DeviceModel).HasMaxLength(64);
            e.Property(s => s.OwnerDevice).HasMaxLength(100);
            e.Property(s => s.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.SummaryJson).HasColumnType("jsonb");
            e.HasOne(s => s.Patient).WithMany(p => p.Sessions).HasForeignKey(s => s.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.PatientId, s.StartedAt });
            e.HasIndex(s => s.StartedAt);
        });

        b.Entity<SensorChunkEntity>(e =>
        {
            e.ToTable("sensor_chunks");
            e.HasKey(c => new { c.SessionId, c.Kind, c.Sequence });
            e.Property(c => c.SessionId).HasMaxLength(64);
            e.HasOne<SessionEntity>().WithMany().HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AudioRecordingEntity>(e =>
        {
            e.ToTable("audio_recordings");
            e.Property(a => a.Id).HasMaxLength(64);
            e.Property(a => a.SessionId).HasMaxLength(64);
            e.Property(a => a.FeaturesJson).HasColumnType("jsonb");
            e.Property(a => a.StoragePath).HasMaxLength(300);
            e.HasOne<SessionEntity>().WithMany(s => s.Audio).HasForeignKey(a => a.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AiAnalysisEntity>(e =>
        {
            e.ToTable("ai_analyses");
            e.Property(a => a.Id).HasMaxLength(64);
            e.Property(a => a.SessionId).HasMaxLength(64);
            e.Property(a => a.Model).HasMaxLength(100);
            e.Property(a => a.Language).HasMaxLength(40);
            e.Property(a => a.OperatorId).HasMaxLength(64);
            e.HasOne<SessionEntity>().WithMany(s => s.Analyses).HasForeignKey(a => a.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Property(a => a.Actor).HasMaxLength(100);
            e.Property(a => a.Action).HasMaxLength(60);
            e.Property(a => a.EntityId).HasMaxLength(64);
            e.Property(a => a.IpAddress).HasMaxLength(45);
            e.Property(a => a.UserAgent).HasMaxLength(256);
            e.HasIndex(a => a.At);
        });

        b.Entity<DashboardAccount>(e =>
        {
            e.ToTable("dashboard_accounts");
            e.Property(a => a.Email).HasMaxLength(254);
            e.HasIndex(a => a.Email).IsUnique();
            e.Property(a => a.GoogleSubject).HasMaxLength(255);
            e.HasIndex(a => a.GoogleSubject).IsUnique().HasFilter("\"GoogleSubject\" IS NOT NULL");
            e.Property(a => a.DisplayName).HasMaxLength(200);
            e.Property(a => a.PasswordHash).HasMaxLength(200);
            e.Property(a => a.DecidedBy).HasMaxLength(254);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(a => a.Status);
        });
    }
}
