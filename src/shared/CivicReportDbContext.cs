using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace CivicReport.Shared;

public sealed class CivicReportDbContext : DbContext
{
    public CivicReportDbContext(DbContextOptions<CivicReportDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Phone).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(120);
            entity.Property(e => e.Phone).HasMaxLength(30).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now() at time zone 'utc'");
        });

        modelBuilder.Entity<OtpCode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Phone).HasMaxLength(30).IsRequired();
            entity.Property(e => e.OtpHash).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => e.Phone);
        });

        modelBuilder.Entity<Report>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).HasMaxLength(280);
            entity.Property(e => e.Location).HasColumnType("geography (point)");
            entity.Property(e => e.FileKey).HasMaxLength(200);
            entity.Property(e => e.PublicPhotoUrl).HasMaxLength(400);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now() at time zone 'utc'");
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Category);
            entity.HasOne(e => e.User)
                .WithMany(u => u.Reports)
                .HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Entity).HasMaxLength(100);
            entity.Property(e => e.Action).HasMaxLength(100);
            entity.Property(e => e.MetadataJson).HasColumnType("jsonb");
        });
    }
}
