using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CivicReport.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NetTopologySuite;
using NetTopologySuite.Geometries;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "dev-secret-key";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "civicreport";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "civicreport";
var otpSecret = builder.Configuration["Otp:Secret"] ?? "otp-secret";

builder.Services.AddDbContext<CivicReportDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? builder.Configuration["DATABASE_URL"]
        ?? "Host=localhost;Port=5432;Database=civicreport;Username=postgres;Password=postgres";
    options.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseNetTopologySuite());
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireClaim("is_admin", "true"));
});

var s3Config = new AmazonS3Config
{
    ServiceURL = builder.Configuration["S3:ServiceUrl"],
    ForcePathStyle = true
};
var s3AccessKey = builder.Configuration["S3:AccessKey"] ?? "minio";
var s3SecretKey = builder.Configuration["S3:SecretKey"] ?? "minio123";
var s3Credentials = new BasicAWSCredentials(s3AccessKey, s3SecretKey);

builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(s3Credentials, s3Config));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

await SeedDevUsersAsync(app);

var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

app.MapPost("/auth/request-otp", async (CivicReportDbContext db, RequestOtpRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Phone))
    {
        return Results.BadRequest(new { error = "phone_required" });
    }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Phone == request.Phone);
    if (user is null)
    {
        user = new User
        {
            Id = Guid.NewGuid(),
            Phone = request.Phone,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Usuário" : request.Name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            ReputationScore = 0
        };
        db.Users.Add(user);
    }
    else if (!string.IsNullOrWhiteSpace(request.Name))
    {
        user.Name = request.Name.Trim();
    }

    var otpCode = GenerateOtpCode();
    var hash = HashOtp(otpCode, otpSecret);
    var otp = new OtpCode
    {
        Id = Guid.NewGuid(),
        Phone = request.Phone,
        OtpHash = hash,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    };
    db.OtpCodes.Add(otp);
    await db.SaveChangesAsync();

    var response = new Dictionary<string, object>
    {
        ["message"] = "otp_sent"
    };

    if (app.Environment.IsDevelopment())
    {
        response["otp_code"] = otpCode;
    }

    return Results.Ok(response);
});

app.MapPost("/auth/verify-otp", async (CivicReportDbContext db, VerifyOtpRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.Otp))
    {
        return Results.BadRequest(new { error = "phone_and_otp_required" });
    }

    var hash = HashOtp(request.Otp.Trim(), otpSecret);
    var otp = await db.OtpCodes
        .Where(o => o.Phone == request.Phone && o.UsedAt == null && o.ExpiresAt > DateTimeOffset.UtcNow)
        .OrderByDescending(o => o.CreatedAt)
        .FirstOrDefaultAsync();

    if (otp is null || otp.OtpHash != hash)
    {
        return Results.Unauthorized();
    }

    otp.UsedAt = DateTimeOffset.UtcNow;

    var user = await db.Users.FirstOrDefaultAsync(u => u.Phone == request.Phone);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var token = GenerateJwt(user, jwtKey, jwtIssuer, jwtAudience);
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        token,
        user = new
        {
            user.Id,
            user.Name,
            user.Phone,
            user.ReputationScore,
            user.IsAdmin
        }
    });
});

app.MapPost("/reports/request-upload", async (ClaimsPrincipal principal, IAmazonS3 s3, CivicReportDbContext db, RequestUploadRequest request) =>
{
    var userId = GetUserId(principal);
    if (userId == Guid.Empty)
    {
        return Results.Unauthorized();
    }

    var contentType = request.ContentType ?? "image/jpeg";
    if (contentType != "image/jpeg" && contentType != "image/png")
    {
        return Results.BadRequest(new { error = "invalid_content_type" });
    }

    var extension = contentType == "image/png" ? "png" : "jpg";
    var fileKey = $"{userId}/{Guid.NewGuid():N}.{extension}";
    var bucket = builder.Configuration["S3:Bucket"] ?? "civicreport";

    var presignRequest = new GetPreSignedUrlRequest
    {
        BucketName = bucket,
        Key = fileKey,
        Verb = HttpVerb.PUT,
        Expires = DateTime.UtcNow.AddMinutes(15),
        ContentType = contentType
    };

    var uploadUrl = s3.GetPreSignedURL(presignRequest);

    return Results.Ok(new { uploadUrl, fileKey });
}).RequireAuthorization();

app.MapPost("/reports", async (ClaimsPrincipal principal, CivicReportDbContext db, CreateReportRequest request) =>
{
    var userId = GetUserId(principal);
    if (userId == Guid.Empty)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 280)
    {
        return Results.BadRequest(new { error = "invalid_description" });
    }

    if (request.AccuracyMeters <= 0)
    {
        return Results.BadRequest(new { error = "invalid_accuracy" });
    }

    var publicBase = builder.Configuration["S3:PublicUrlBase"] ?? "http://localhost:9000";
    var bucket = builder.Configuration["S3:Bucket"] ?? "civicreport";

    var report = new Report
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Category = request.Category,
        Description = request.Description.Trim(),
        AccuracyMeters = request.AccuracyMeters,
        FileKey = request.FileKey,
        PublicPhotoUrl = $"{publicBase.TrimEnd('/')}/{bucket}/{request.FileKey}",
        Location = geometryFactory.CreatePoint(new Coordinate(request.Lng, request.Lat)),
        Status = ReportStatus.PendingModeration,
        CreatedAt = DateTimeOffset.UtcNow
    };

    db.Reports.Add(report);
    db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        Entity = "Report",
        EntityId = report.Id,
        Action = "CREATED",
        ActorUserId = userId,
        MetadataJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow
    });

    await db.SaveChangesAsync();

    return Results.Ok(new { report.Id });
}).RequireAuthorization();

app.MapGet("/feed", async (CivicReportDbContext db, string? category, string? bbox, DateTimeOffset? since, int page = 1, int pageSize = 20) =>
{
    var query = db.Reports.AsNoTracking().Where(r => r.Status == ReportStatus.Approved);

    if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<ReportCategory>(category, true, out var parsedCategory))
    {
        query = query.Where(r => r.Category == parsedCategory);
    }

    if (since.HasValue)
    {
        query = query.Where(r => r.CreatedAt >= since.Value);
    }

    if (!string.IsNullOrWhiteSpace(bbox))
    {
        var parts = bbox.Split(',');
        if (parts.Length == 4 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minLng) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minLat) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxLng) &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxLat))
        {
            query = query.Where(r => r.Location.X >= minLng && r.Location.X <= maxLng && r.Location.Y >= minLat && r.Location.Y <= maxLat);
        }
    }

    var results = await query
        .OrderByDescending(r => r.CreatedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(r => new
        {
            r.Id,
            category = r.Category.ToString().ToUpperInvariant(),
            r.Description,
            lat = r.Location.Y,
            lng = r.Location.X,
            r.CreatedAt,
            status = r.Status.ToString().ToUpperInvariant(),
            photoUrl = r.PublicPhotoUrl
        })
        .ToListAsync();

    return Results.Ok(results);
});

app.MapGet("/reports/{id:guid}", async (CivicReportDbContext db, Guid id) =>
{
    var report = await db.Reports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.Status == ReportStatus.Approved);
    if (report is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        report.Id,
        category = report.Category.ToString().ToUpperInvariant(),
        report.Description,
        lat = report.Location.Y,
        lng = report.Location.X,
        report.CreatedAt,
        status = report.Status.ToString().ToUpperInvariant(),
        photoUrl = report.PublicPhotoUrl
    });
});

app.MapGet("/admin/reports/review", async (CivicReportDbContext db, string status) =>
{
    if (!Enum.TryParse<ReportStatus>(status, true, out var parsed))
    {
        return Results.BadRequest(new { error = "invalid_status" });
    }

    var reports = await db.Reports
        .AsNoTracking()
        .Where(r => r.Status == parsed)
        .OrderBy(r => r.CreatedAt)
        .Select(r => new
        {
            r.Id,
            category = r.Category.ToString().ToUpperInvariant(),
            r.Description,
            lat = r.Location.Y,
            lng = r.Location.X,
            r.AccuracyMeters,
            r.CreatedAt,
            status = r.Status.ToString().ToUpperInvariant(),
            r.PublicPhotoUrl
        })
        .ToListAsync();

    return Results.Ok(reports);
}).RequireAuthorization("AdminOnly");

app.MapGet("/admin/reports", async (CivicReportDbContext db, string? category, string? status, DateTimeOffset? from, DateTimeOffset? to) =>
{
    var query = db.Reports.AsNoTracking().AsQueryable();

    if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<ReportCategory>(category, true, out var parsedCategory))
    {
        query = query.Where(r => r.Category == parsedCategory);
    }

    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ReportStatus>(status, true, out var parsedStatus))
    {
        query = query.Where(r => r.Status == parsedStatus);
    }

    if (from.HasValue)
    {
        query = query.Where(r => r.CreatedAt >= from.Value);
    }

    if (to.HasValue)
    {
        query = query.Where(r => r.CreatedAt <= to.Value);
    }

    var results = await query.OrderByDescending(r => r.CreatedAt)
        .Select(r => new
        {
            r.Id,
            category = r.Category.ToString().ToUpperInvariant(),
            r.Description,
            lat = r.Location.Y,
            lng = r.Location.X,
            r.AccuracyMeters,
            r.CreatedAt,
            r.ValidatedAt,
            status = r.Status.ToString().ToUpperInvariant(),
            r.PublicPhotoUrl
        })
        .ToListAsync();

    return Results.Ok(results);
}).RequireAuthorization("AdminOnly");

app.MapGet("/admin/reports/export.csv", async (CivicReportDbContext db, string? category, string? status, DateTimeOffset? from, DateTimeOffset? to) =>
{
    var query = db.Reports.AsNoTracking().Include(r => r.User).AsQueryable();

    if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<ReportCategory>(category, true, out var parsedCategory))
    {
        query = query.Where(r => r.Category == parsedCategory);
    }

    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ReportStatus>(status, true, out var parsedStatus))
    {
        query = query.Where(r => r.Status == parsedStatus);
    }

    if (from.HasValue)
    {
        query = query.Where(r => r.CreatedAt >= from.Value);
    }

    if (to.HasValue)
    {
        query = query.Where(r => r.CreatedAt <= to.Value);
    }

    var reports = await query.OrderByDescending(r => r.CreatedAt).ToListAsync();

    var sb = new StringBuilder();
    sb.AppendLine("id,category,description,lat,lng,accuracy,status,created_at,validated_at,user_phone");

    foreach (var report in reports)
    {
        var phone = MaskPhone(report.User?.Phone ?? string.Empty);
        sb.AppendLine(string.Join(',', new[]
        {
            report.Id.ToString(),
            report.Category.ToString().ToUpperInvariant(),
            CsvEscape(report.Description),
            report.Location.Y.ToString(CultureInfo.InvariantCulture),
            report.Location.X.ToString(CultureInfo.InvariantCulture),
            report.AccuracyMeters.ToString(CultureInfo.InvariantCulture),
            report.Status.ToString().ToUpperInvariant(),
            report.CreatedAt.ToString("O"),
            report.ValidatedAt?.ToString("O") ?? string.Empty,
            phone
        }));
    }

    return Results.Text(sb.ToString(), "text/csv");
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/reports/{id:guid}/approve", async (ClaimsPrincipal principal, CivicReportDbContext db, Guid id) =>
{
    var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id);
    if (report is null)
    {
        return Results.NotFound();
    }

    report.Status = ReportStatus.Approved;
    report.ValidatedAt = DateTimeOffset.UtcNow;

    db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        Entity = "Report",
        EntityId = report.Id,
        Action = "APPROVED_MANUAL",
        ActorUserId = GetUserId(principal),
        MetadataJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow
    });

    await db.SaveChangesAsync();

    return Results.Ok();
}).RequireAuthorization("AdminOnly");

app.MapPost("/admin/reports/{id:guid}/reject", async (ClaimsPrincipal principal, CivicReportDbContext db, Guid id, RejectReportRequest request) =>
{
    var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id);
    if (report is null)
    {
        return Results.NotFound();
    }

    report.Status = ReportStatus.Rejected;
    report.ValidatedAt = DateTimeOffset.UtcNow;
    report.ModerationReason = request.Reason;

    db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        Entity = "Report",
        EntityId = report.Id,
        Action = "REJECTED_MANUAL",
        ActorUserId = GetUserId(principal),
        MetadataJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow
    });

    await db.SaveChangesAsync();

    return Results.Ok();
}).RequireAuthorization("AdminOnly");

app.Run();

static Guid GetUserId(ClaimsPrincipal principal)
{
    var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
    return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
}

static string GenerateJwt(User user, string key, string issuer, string audience)
{
    var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new(JwtRegisteredClaimNames.PhoneNumber, user.Phone),
        new("name", user.Name),
        new("is_admin", user.IsAdmin ? "true" : "false")
    };

    var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(issuer, audience, claims, expires: DateTime.UtcNow.AddHours(12), signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(token);
}

static string GenerateOtpCode()
{
    var number = RandomNumberGenerator.GetInt32(100000, 999999);
    return number.ToString(CultureInfo.InvariantCulture);
}

static string HashOtp(string otp, string secret)
{
    using var sha = SHA256.Create();
    var bytes = Encoding.UTF8.GetBytes($"{otp}:{secret}");
    var hash = sha.ComputeHash(bytes);
    return Convert.ToHexString(hash);
}

static string CsvEscape(string value)
{
    var escaped = value.Replace("\"", "\"\"");
    return $"\"{escaped}\"";
}

static string MaskPhone(string phone)
{
    if (phone.Length <= 4)
    {
        return phone;
    }

    return new string('*', phone.Length - 4) + phone[^4..];
}

static async Task SeedDevUsersAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CivicReportDbContext>();
    await db.Database.MigrateAsync();

    if (app.Environment.IsDevelopment())
    {
        if (!await db.Users.AnyAsync())
        {
            db.Users.AddRange(new User
            {
                Id = Guid.NewGuid(),
                Name = "Admin Dev",
                Phone = "+5511990000000",
                IsAdmin = true,
                ReputationScore = 0,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new User
            {
                Id = Guid.NewGuid(),
                Name = "Usuário Dev",
                Phone = "+5511990000001",
                IsAdmin = false,
                ReputationScore = 0,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }
    }
}

record RequestOtpRequest(string Phone, string? Name);
record VerifyOtpRequest(string Phone, string Otp);
record RequestUploadRequest(string? ContentType);
record CreateReportRequest(ReportCategory Category, string Description, double Lat, double Lng, double AccuracyMeters, string FileKey);
record RejectReportRequest(string? Reason);
