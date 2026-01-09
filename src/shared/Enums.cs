namespace CivicReport.Shared;

public enum ReportCategory
{
    Dengue = 0,
    Buraco = 1,
    MatoAlto = 2,
    Lixo = 3
}

public enum ReportStatus
{
    PendingModeration = 0,
    Approved = 1,
    Rejected = 2,
    NeedsReview = 3,
    Resolved = 4
}
