using System.Net.Mail;
using StatusHub.Domain;

namespace StatusHub.Api;

public sealed record MonitorRequest(
    string? Name, string? Url, string? Method, int? IntervalMinutes, int? ExpectedStatus,
    string? Keyword, int? TimeoutSeconds, bool? Active);

public sealed record MonitorDto(
    string Id, string Name, string Url, string Method, int IntervalMinutes, int ExpectedStatus, string? Keyword,
    int TimeoutSeconds, bool Active, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LastCheckedAt,
    string? OpenIncidentId, double? Availability30d)
{
    public static MonitorDto From(UptimeMonitor m, double? availability30d = null) => new(
        m.MonitorId, m.Name, m.Url, m.Method, m.IntervalMinutes, m.ExpectedStatus, m.Keyword, m.TimeoutSeconds, m.Active,
        m.Active ? m.Status.ToString() : "Paused", m.CreatedAt, m.LastCheckedAt, m.OpenIncidentId, availability30d);
}

public sealed record StatsDto(double? Availability24h, double? Availability7d, double? Availability30d, int? P50Ms24h, int? P95Ms24h, int Checks24h);
public sealed record LatencyPointDto(DateTimeOffset T, int P50, int P95, int Total, int Failures);
public sealed record DayDto(DateOnly Date, double? Availability, int Total);
public sealed record IncidentDto(string Id, string MonitorId, string? MonitorName, DateTimeOffset OpenedAt, DateTimeOffset? ResolvedAt, double? DurationSeconds, string Cause);

public sealed record AlertRequest(string? Type, string? Target, string? Secret, bool? Active);
public sealed record AlertDto(string Id, string Type, string Target, bool HasSecret, bool Active, string? Secret = null);

public sealed record SlugRequest(string? Slug);
public sealed record MeDto(string UserId, string Email, string Slug);
public sealed record DemoRequest(string? Mode);
public sealed record DemoDto(string Mode, string TargetUrl);
public sealed record DevTokenRequest(string? Email);

public sealed record StatusServiceDto(string Name, string Status, double? Availability90d, IReadOnlyList<DayDto> Days);
public sealed record StatusPageDto(string Slug, string Overall, DateTimeOffset GeneratedAt, IReadOnlyList<StatusServiceDto> Services, IReadOnlyList<IncidentDto> Incidents);

public static class Validation
{
    public static readonly string[] Methods = ["GET", "HEAD"];

    /// <summary>Validates a create/update request and returns the normalized monitor fields.</summary>
    public static Dictionary<string, string[]> Check(MonitorRequest r, bool allowPrivate)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 100) e["name"] = ["Le nom est requis (100 caracteres max)."];
        if (!UrlGuard.TryValidate(r.Url, allowPrivate, out var urlError)) e["url"] = [urlError ?? "URL invalide."];
        if (r.Method is not null && !Methods.Contains(r.Method.ToUpperInvariant())) e["method"] = ["Methode autorisee : GET ou HEAD."];
        if (r.IntervalMinutes is { } i && i is < UptimeMonitor.MinInterval or > UptimeMonitor.MaxInterval) e["intervalMinutes"] = ["L'intervalle doit etre entre 1 et 5 minutes."];
        if (r.ExpectedStatus is { } s && s is < 100 or > 599) e["expectedStatus"] = ["Code HTTP attendu invalide."];
        if (r.Keyword is { Length: > 200 }) e["keyword"] = ["Mot-cle trop long (200 max)."];
        if (r.TimeoutSeconds is { } t && t is < 1 or > 30) e["timeoutSeconds"] = ["Le timeout doit etre entre 1 et 30 secondes."];
        if (r.Method?.ToUpperInvariant() == "HEAD" && !string.IsNullOrEmpty(r.Keyword)) e["keyword"] = ["Un mot-cle necessite la methode GET."];
        return e;
    }

    public static Dictionary<string, string[]> CheckAlert(AlertRequest r, bool allowPrivate, bool creating)
    {
        var e = new Dictionary<string, string[]>();
        if (!Enum.TryParse<AlertType>(r.Type, ignoreCase: true, out var type)) { e["type"] = ["Type : email ou webhook."]; return e; }
        if (type == AlertType.Email)
        {
            if (string.IsNullOrWhiteSpace(r.Target) || r.Target.Length > 254 || !MailAddress.TryCreate(r.Target, out var a) || a.Address != r.Target)
                e["target"] = ["Adresse email invalide."];
        }
        else if (!UrlGuard.TryValidate(r.Target, allowPrivate, out var err)) e["target"] = [err ?? "URL invalide."];
        if (r.Secret is { Length: > 200 }) e["secret"] = ["Secret trop long."];
        return e;
    }
}
