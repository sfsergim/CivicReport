using CivicReport.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Services.AddDbContext<CivicReportDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? builder.Configuration["DATABASE_URL"]
        ?? "Host=localhost;Port=5432;Database=civicreport;Username=postgres;Password=postgres";
    options.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseNetTopologySuite());
});

builder.Services.AddHostedService<ModerationWorker>();

var host = builder.Build();
await host.RunAsync();

sealed class ModerationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ModerationWorker> _logger;

    public ModerationWorker(IServiceProvider serviceProvider, ILogger<ModerationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessReportsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing moderation queue");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private async Task ProcessReportsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CivicReportDbContext>();

        var pending = await db.Reports
            .Include(r => r.User)
            .Where(r => r.Status == ReportStatus.PendingModeration)
            .OrderBy(r => r.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var report in pending)
        {
            var reason = EvaluateReport(db, report);
            if (reason is null)
            {
                report.Status = ReportStatus.Approved;
                report.ModerationScore = 0.9;
                report.ValidatedAt = DateTimeOffset.UtcNow;
                report.ModerationReason = null;
                db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    Entity = "Report",
                    EntityId = report.Id,
                    Action = "APPROVED_AUTO",
                    ActorUserId = null,
                    MetadataJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                report.Status = ReportStatus.NeedsReview;
                report.ModerationScore = 0.4;
                report.ModerationReason = reason;
                report.ValidatedAt = null;
                db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    Entity = "Report",
                    EntityId = report.Id,
                    Action = "NEEDS_REVIEW_AUTO",
                    ActorUserId = null,
                    MetadataJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? EvaluateReport(CivicReportDbContext db, Report report)
    {
        if (report.Description.Contains("http", StringComparison.OrdinalIgnoreCase) ||
            report.Description.Contains("www", StringComparison.OrdinalIgnoreCase))
        {
            return "description_contains_link";
        }

        if (HasRepeatedWords(report.Description))
        {
            return "description_repetition";
        }

        if (report.AccuracyMeters > 100)
        {
            return "accuracy_too_low";
        }

        var today = DateTimeOffset.UtcNow.Date;
        var userDailyCount = db.Reports.Count(r => r.UserId == report.UserId && r.CreatedAt.Date == today);
        if (userDailyCount > 3)
        {
            return "daily_limit_exceeded";
        }

        return null;
    }

    private static bool HasRepeatedWords(string description)
    {
        var words = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3)
        {
            return false;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            counts.TryGetValue(word, out var current);
            counts[word] = current + 1;
            if (counts[word] >= 3)
            {
                return true;
            }
        }

        return false;
    }
}
