using NetTopologySuite.Geometries;

namespace CivicReport.Shared;

public sealed class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public int ReputationScore { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Report> Reports { get; set; } = new List<Report>();
}

public sealed class OtpCode
{
    public Guid Id { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string OtpHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Report
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public ReportCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public Point Location { get; set; } = default!;
    public double AccuracyMeters { get; set; }
    public string FileKey { get; set; } = string.Empty;
    public string PublicPhotoUrl { get; set; } = string.Empty;
    public ReportStatus Status { get; set; }
    public double? ModerationScore { get; set; }
    public string? ModerationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ValidatedAt { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public string Entity { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
