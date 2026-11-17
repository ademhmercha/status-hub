using System.Security.Cryptography;
using Microsoft.AspNetCore.RateLimiting;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Api;

public static class Endpoints
{
    public const string ApiPolicy = "api";
    public const string PublicPolicy = "public";
    public const string DemoPolicy = "demo";
    public const string TokenPolicy = "token";
    public const int MaxAlertsPerAccount = 10;

    public static void MapStatusHub(this WebApplication app)
    {
        var cfg = app.Configuration;
        var allowPrivate = cfg.GetValue<bool>("Checker:AllowPrivateTargets");

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        MapDemoTarget(app);
        MapPublic(app);

        if (cfg.GetValue<bool>("Auth:DevTokens") && !string.IsNullOrEmpty(cfg["Auth:DevSigningKey"]))
        {
            var key = cfg["Auth:DevSigningKey"]!;
            app.MapPost("/dev/token", (DevTokenRequest r) =>
                    string.IsNullOrWhiteSpace(r.Email) || !r.Email.Contains('@') || r.Email.Length > 254
                        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Email invalide."] })
                        : Results.Ok(new { token = AuthSetup.CreateDevToken(key, r.Email.Trim()) }))
                .AllowAnonymous().RequireRateLimiting(TokenPolicy).ExcludeFromDescription();
        }

        var api = app.MapGroup("/api/v1").RequireAuthorization().RequireRateLimiting(ApiPolicy);
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var sub = http.User.FindFirst("sub")?.Value;
            var email = http.User.FindFirst("email")?.Value;
            if (sub is null || email is null) return Results.Problem("Le jeton doit contenir les claims sub et email (utilisez l'ID token).", statusCode: 401);
            var account = await http.RequestServices.GetRequiredService<AccountProvisioner>().EnsureAsync(sub, email, http.RequestAborted);
            http.SetUser(new CurrentUser(sub, email, account.Slug));
            return await next(ctx);
        });

        MapMe(api);
        MapMonitors(api, allowPrivate);
        MapAlerts(api, allowPrivate);
        MapDemo(api, cfg);
    }

    private static void MapDemoTarget(WebApplication app) =>
        app.MapGet("/demo/target", async (IDemoStateStore state, CancellationToken ct) =>
        {
            switch (await state.GetAsync(ct))
            {
                case DemoMode.Down: return Results.Text("StatusHub demo target: DOWN", statusCode: StatusCodes.Status503ServiceUnavailable);
                case DemoMode.Slow:
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    return Results.Text("StatusHub demo target: OK (slow)");
                default: return Results.Text("StatusHub demo target: OK");
            }
        }).AllowAnonymous();

    private static void MapMe(RouteGroupBuilder api)
    {
        api.MapGet("/me", (HttpContext c) => Results.Ok(new MeDto(c.GetUser().UserId, c.GetUser().Email, c.GetUser().Slug)));
        api.MapPut("/me/slug", async (SlugRequest r, HttpContext c, IAccountRepository accounts, CancellationToken ct) =>
        {
            var u = c.GetUser();
            if (!SlugRules.IsValid(r.Slug))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slug"] = ["3 a 30 caracteres : a-z, 0-9 et tirets, sans tiret au debut/fin, mot reserve interdit."] });
            if (!await accounts.ChangeSlugAsync(u.UserId, r.Slug!, ct))
                return Results.Problem("Ce slug est deja utilise.", statusCode: 409, title: "Conflict");
            AccountProvisioner.Forget(u.UserId);
            return Results.Ok(new MeDto(u.UserId, u.Email, r.Slug!));
        });
    }

    private static void MapMonitors(RouteGroupBuilder api, bool allowPrivate)
    {
        var g = api.MapGroup("/monitors");

        g.MapGet("/", async (HttpContext c, IMonitorRepository monitors, IAggregateRepository aggs, TimeProvider clock, CancellationToken ct) =>
        {
            var list = await monitors.ListAsync(c.GetUser().UserId, ct);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var dtos = await Task.WhenAll(list.Select(async m =>
                MonitorDto.From(m, Availability(await aggs.GetRangeAsync(m.MonitorId, today.AddDays(-29), today, ct)))));
            return Results.Ok(dtos);
        });

        g.MapPost("/", async (MonitorRequest r, HttpContext c, IMonitorRepository monitors, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = Validation.Check(r, allowPrivate);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var u = c.GetUser();
            if ((await monitors.ListAsync(u.UserId, ct)).Count >= UptimeMonitor.MaxPerAccount)
                return Results.Problem($"Limite de {UptimeMonitor.MaxPerAccount} moniteurs atteinte.", statusCode: 409, title: "Limit reached");
            var m = new UptimeMonitor(u.UserId, Guid.NewGuid().ToString("N"), r.Name!.Trim(), r.Url!.Trim(),
                (r.Method ?? "GET").ToUpperInvariant(), r.IntervalMinutes ?? 1, r.ExpectedStatus ?? 200,
                string.IsNullOrWhiteSpace(r.Keyword) ? null : r.Keyword, r.TimeoutSeconds ?? 10, r.Active ?? true, clock.GetUtcNow());
            await monitors.CreateAsync(m, ct);
            return Results.Created($"/api/v1/monitors/{m.MonitorId}", MonitorDto.From(m));
        });

        g.MapGet("/{id}", async (string id, HttpContext c, IMonitorRepository monitors, CancellationToken ct) =>
            await monitors.GetAsync(c.GetUser().UserId, id, ct) is { } m ? Results.Ok(MonitorDto.From(m)) : NotFound());

        g.MapPut("/{id}", async (string id, MonitorRequest r, HttpContext c, IMonitorRepository monitors, CancellationToken ct) =>
        {
            var errors = Validation.Check(r, allowPrivate);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var m = await monitors.GetAsync(c.GetUser().UserId, id, ct);
            if (m is null) return NotFound();
            var updated = m with
            {
                Name = r.Name!.Trim(), Url = r.Url!.Trim(), Method = (r.Method ?? m.Method).ToUpperInvariant(),
                IntervalMinutes = r.IntervalMinutes ?? m.IntervalMinutes, ExpectedStatus = r.ExpectedStatus ?? m.ExpectedStatus,
                Keyword = string.IsNullOrWhiteSpace(r.Keyword) ? null : r.Keyword, TimeoutSeconds = r.TimeoutSeconds ?? m.TimeoutSeconds,
                Active = r.Active ?? m.Active,
            };
            await monitors.UpdateAsync(updated, ct);
            return Results.Ok(MonitorDto.From(updated));
        });

        g.MapDelete("/{id}", async (string id, HttpContext c, IMonitorRepository monitors, CancellationToken ct) =>
        {
            var u = c.GetUser();
            if (await monitors.GetAsync(u.UserId, id, ct) is null) return NotFound();
            await monitors.DeleteAsync(u.UserId, id, ct);
            return Results.NoContent();
        });

        g.MapPost("/{id}/pause", (string id, HttpContext c, IMonitorRepository m, CancellationToken ct) => SetActive(id, false, c, m, ct));
        g.MapPost("/{id}/resume", (string id, HttpContext c, IMonitorRepository m, CancellationToken ct) => SetActive(id, true, c, m, ct));

        g.MapGet("/{id}/stats", async (string id, HttpContext c, IMonitorRepository monitors, IResultRepository results, IAggregateRepository aggs, TimeProvider clock, CancellationToken ct) =>
        {
            if (await monitors.GetAsync(c.GetUser().UserId, id, ct) is null) return NotFound();
            var now = clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var raw = await results.GetRangeAsync(id, now.AddHours(-24), now, ct);
            var days = await aggs.GetRangeAsync(id, today.AddDays(-29), today, ct);
            var lat = raw.Where(r => r.Success).Select(r => r.LatencyMs).Order().ToArray();
            return Results.Ok(new StatsDto(
                raw.Count == 0 ? null : Math.Round(100.0 * raw.Count(r => r.Success) / raw.Count, 3),
                Availability(days.Where(d => d.Date >= today.AddDays(-6)).ToList()),
                Availability(days),
                lat.Length == 0 ? null : lat[(int)Math.Ceiling(lat.Length * 0.5) - 1],
                lat.Length == 0 ? null : lat[(int)Math.Ceiling(lat.Length * 0.95) - 1],
                raw.Count));
        });

        g.MapGet("/{id}/latency", async (string id, string? range, HttpContext c, IMonitorRepository monitors, IResultRepository results, IAggregateRepository aggs, TimeProvider clock, CancellationToken ct) =>
        {
            if (await monitors.GetAsync(c.GetUser().UserId, id, ct) is null) return NotFound();
            var now = clock.GetUtcNow();
            if (range is "7d" or "30d")
            {
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                var days = await aggs.GetRangeAsync(id, today.AddDays(range == "7d" ? -6 : -29), today, ct);
                return Results.Ok(days.Select(d => new LatencyPointDto(new DateTimeOffset(d.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), d.P50, d.P95, d.Total, d.Total - d.Successes)));
            }
            // 24 h: exact percentiles per 15-minute bucket from raw results.
            var raw = await results.GetRangeAsync(id, now.AddHours(-24), now, ct);
            var points = raw.GroupBy(r => r.Timestamp.UtcTicks / TimeSpan.FromMinutes(15).Ticks).OrderBy(g2 => g2.Key).Select(g2 =>
            {
                var l = g2.Where(r => r.Success).Select(r => r.LatencyMs).Order().ToArray();
                int P(double p) => l.Length == 0 ? 0 : l[(int)Math.Ceiling(l.Length * p) - 1];
                return new LatencyPointDto(new DateTimeOffset(g2.Key * TimeSpan.FromMinutes(15).Ticks, TimeSpan.Zero), P(0.5), P(0.95), g2.Count(), g2.Count(r => !r.Success));
            });
            return Results.Ok(points);
        });

        g.MapGet("/{id}/availability", async (string id, int? days, HttpContext c, IMonitorRepository monitors, IAggregateRepository aggs, TimeProvider clock, CancellationToken ct) =>
        {
            if (await monitors.GetAsync(c.GetUser().UserId, id, ct) is null) return NotFound();
            return Results.Ok(await DaySeries(aggs, id, Math.Clamp(days ?? 90, 1, 90), clock, ct));
        });

        g.MapGet("/{id}/incidents", async (string id, int? limit, HttpContext c, IMonitorRepository monitors, IIncidentRepository incidents, CancellationToken ct) =>
        {
            var m = await monitors.GetAsync(c.GetUser().UserId, id, ct);
            if (m is null) return NotFound();
            return Results.Ok((await incidents.ListForMonitorAsync(id, Math.Clamp(limit ?? 50, 1, 100), ct)).Select(i => ToDto(i, m.Name)));
        });
    }

    private static void MapAlerts(RouteGroupBuilder api, bool allowPrivate)
    {
        var g = api.MapGroup("/alerts");

        g.MapGet("/", async (HttpContext c, IAlertRepository alerts, CancellationToken ct) =>
            Results.Ok((await alerts.ListAsync(c.GetUser().UserId, ct)).Select(s => ToDto(s))));

        g.MapPost("/", async (AlertRequest r, HttpContext c, IAlertRepository alerts, CancellationToken ct) =>
        {
            var errors = Validation.CheckAlert(r, allowPrivate, creating: true);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var u = c.GetUser();
            if ((await alerts.ListAsync(u.UserId, ct)).Count >= MaxAlertsPerAccount)
                return Results.Problem($"Limite de {MaxAlertsPerAccount} alertes atteinte.", statusCode: 409, title: "Limit reached");
            var type = Enum.Parse<AlertType>(r.Type!, ignoreCase: true);
            var secret = type == AlertType.Webhook ? (string.IsNullOrWhiteSpace(r.Secret) ? NewSecret() : r.Secret) : null;
            var sub = new AlertSubscription(u.UserId, Guid.NewGuid().ToString("N"), type, r.Target!.Trim(), secret, r.Active ?? true);
            await alerts.CreateAsync(sub, ct);
            return Results.Created($"/api/v1/alerts/{sub.SubscriptionId}", ToDto(sub, revealSecret: true)); // the secret is shown only once
        });

        g.MapPut("/{id}", async (string id, AlertRequest r, HttpContext c, IAlertRepository alerts, CancellationToken ct) =>
        {
            var errors = Validation.CheckAlert(r, allowPrivate, creating: false);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var existing = await alerts.GetAsync(c.GetUser().UserId, id, ct);
            if (existing is null) return NotFound();
            var type = Enum.Parse<AlertType>(r.Type!, ignoreCase: true);
            var updated = existing with
            {
                Type = type, Target = r.Target!.Trim(), Active = r.Active ?? existing.Active,
                Secret = type == AlertType.Webhook ? (string.IsNullOrWhiteSpace(r.Secret) ? existing.Secret ?? NewSecret() : r.Secret) : null,
            };
            await alerts.UpdateAsync(updated, ct);
            return Results.Ok(ToDto(updated));
        });

        g.MapDelete("/{id}", async (string id, HttpContext c, IAlertRepository alerts, CancellationToken ct) =>
        {
            var u = c.GetUser();
            if (await alerts.GetAsync(u.UserId, id, ct) is null) return NotFound();
            await alerts.DeleteAsync(u.UserId, id, ct);
            return Results.NoContent();
        });

        g.MapPost("/{id}/test", async (string id, HttpContext c, IAlertRepository alerts, IEmailSender email, IWebhookSender hook, TimeProvider clock, CancellationToken ct) =>
        {
            var u = c.GetUser();
            var sub = await alerts.GetAsync(u.UserId, id, ct);
            if (sub is null) return NotFound();
            var evt = new IncidentEvent(u.UserId, "test", "Moniteur de test", "https://statushub.example", "test", IncidentEventType.Opened,
                clock.GetUtcNow(), "Ceci est un message de test StatusHub", null);
            try
            {
                await new AlertDispatcher(email, hook, new NotifierOptions([])).DispatchAsync(sub, evt, ct);
                return Results.Ok(new { sent = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"Envoi impossible : {ex.Message}", statusCode: 502, title: "Delivery failed");
            }
        }).RequireRateLimiting(DemoPolicy);
    }

    private static void MapDemo(RouteGroupBuilder api, IConfiguration cfg)
    {
        if (!cfg.GetValue<bool>("Demo:Enabled")) return;
        var g = api.MapGroup("/demo").RequireRateLimiting(DemoPolicy);
        string Target(HttpContext c) => cfg["Demo:TargetUrl"] ?? $"{c.Request.Scheme}://{c.Request.Host}/demo/target";

        g.MapGet("/", async (HttpContext c, IDemoStateStore s, CancellationToken ct) => Results.Ok(new DemoDto((await s.GetAsync(ct)).ToString().ToLowerInvariant(), Target(c))));
        g.MapPut("/", async (DemoRequest r, HttpContext c, IDemoStateStore s, CancellationToken ct) =>
        {
            if (!Enum.TryParse<DemoMode>(r.Mode, ignoreCase: true, out var mode))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mode"] = ["Mode : ok, slow ou down."] });
            await s.SetAsync(mode, ct);
            return Results.Ok(new DemoDto(mode.ToString().ToLowerInvariant(), Target(c)));
        });
    }

    /// <summary>Public status page: no authentication, cached 30 s.</summary>
    private static void MapPublic(WebApplication app) =>
        app.MapGet("/api/v1/status/{slug}", async (string slug, HttpContext http, IAccountRepository accounts, IMonitorRepository monitors,
                IAggregateRepository aggs, IIncidentRepository incidents, TimeProvider clock, CancellationToken ct) =>
            {
                var account = SlugRules.IsValid(slug) ? await accounts.GetBySlugAsync(slug, ct) : null;
                if (account is null) return NotFound();

                var active = (await monitors.ListAsync(account.UserId, ct)).Where(m => m.Active).ToList();
                var services = await Task.WhenAll(active.Select(async m =>
                {
                    var days = await DaySeries(aggs, m.MonitorId, 90, clock, ct);
                    var total = days.Sum(d => d.Total);
                    var avail = total == 0 ? (double?)null : Math.Round(days.Where(d => d.Availability is not null).Sum(d => d.Availability!.Value * d.Total) / total, 3);
                    return new StatusServiceDto(m.Name, m.Status.ToString(), avail, days);
                }));
                var recent = (await Task.WhenAll(active.Select(async m => (await incidents.ListForMonitorAsync(m.MonitorId, 10, ct)).Select(i => ToDto(i, m.Name)))))
                    .SelectMany(x => x).OrderByDescending(i => i.OpenedAt).Take(20).ToList();

                var overall = services.Length == 0 || services.All(s => s.Status == nameof(MonitorStatus.Unknown)) ? "unknown"
                    : services.All(s => s.Status == nameof(MonitorStatus.Down)) ? "major_outage"
                    : services.Any(s => s.Status == nameof(MonitorStatus.Down)) ? "partial_outage" : "operational";

                http.Response.Headers.CacheControl = "public, max-age=30";
                return Results.Ok(new StatusPageDto(account.Slug, overall, clock.GetUtcNow(), services, recent));
            })
            .AllowAnonymous().RequireRateLimiting(PublicPolicy).CacheOutput(p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByRouteValue("slug"));

    private static async Task<IResult> SetActive(string id, bool active, HttpContext c, IMonitorRepository monitors, CancellationToken ct)
    {
        var m = await monitors.GetAsync(c.GetUser().UserId, id, ct);
        if (m is null) return NotFound();
        var updated = m with { Active = active };
        await monitors.UpdateAsync(updated, ct);
        return Results.Ok(MonitorDto.From(updated));
    }

    private static async Task<IReadOnlyList<DayDto>> DaySeries(IAggregateRepository aggs, string id, int days, TimeProvider clock, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var byDay = (await aggs.GetRangeAsync(id, today.AddDays(-(days - 1)), today, ct)).ToDictionary(a => a.Date);
        return Enumerable.Range(0, days).Select(i =>
        {
            var d = today.AddDays(-(days - 1) + i);
            return byDay.TryGetValue(d, out var a) && a.Total > 0 ? new DayDto(d, Math.Round(a.Availability, 3), a.Total) : new DayDto(d, null, 0);
        }).ToList();
    }

    private static double? Availability(IReadOnlyList<DailyAggregate> days)
    {
        var total = days.Sum(d => d.Total);
        return total == 0 ? null : Math.Round(100.0 * days.Sum(d => d.Successes) / total, 3);
    }

    private static IncidentDto ToDto(Incident i, string? monitorName) =>
        new(i.IncidentId, i.MonitorId, monitorName, i.OpenedAt, i.ResolvedAt, i.Duration?.TotalSeconds, i.Cause);

    private static AlertDto ToDto(AlertSubscription s, bool revealSecret = false) =>
        new(s.SubscriptionId, s.Type.ToString().ToLowerInvariant(), s.Target, s.Secret is not null, s.Active, revealSecret ? s.Secret : null);

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static IResult NotFound() => Results.Problem("Ressource introuvable.", statusCode: 404, title: "Not found");
}
