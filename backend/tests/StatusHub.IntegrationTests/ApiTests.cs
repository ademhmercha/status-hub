using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.IntegrationTests;

[Collection("dynamo")]
public class ApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly object ValidMonitor = new { name = "Site", url = "https://example.com", intervalMinutes = 1 };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>());

    private async Task<(HttpClient Client, string UserId)> NewUser()
    {
        var c = await api.ClientForAsync(ApiFixture.NewEmail());
        var me = await Json(await c.GetAsync("/api/v1/me"));
        return (c, me.GetProperty("userId").GetString()!);
    }

    private static async Task<string> CreateMonitor(HttpClient c, object? body = null)
    {
        var res = await c.PostAsJsonAsync("/api/v1/monitors", body ?? ValidMonitor);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Json(res)).GetProperty("id").GetString()!;
    }

    // ---- authentication ----

    [Fact]
    public async Task Unauthenticated_IsRejected()
    {
        var c = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/monitors")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task ForgedToken_IsRejected()
    {
        var c = api.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/monitors")).StatusCode);
    }

    [Fact]
    public async Task FirstCall_ProvisionsAccount_WithSlug_AndDefaultEmailAlert()
    {
        var email = ApiFixture.NewEmail();
        var c = await api.ClientForAsync(email);
        var me = await Json(await c.GetAsync("/api/v1/me"));
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.True(SlugRulesValid(me.GetProperty("slug").GetString()!));

        var alerts = await Json(await c.GetAsync("/api/v1/alerts"));
        var only = Assert.Single(alerts.EnumerateArray());
        Assert.Equal("email", only.GetProperty("type").GetString());
        Assert.Equal(email, only.GetProperty("target").GetString());
    }

    private static bool SlugRulesValid(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "^[a-z0-9][a-z0-9-]{1,28}[a-z0-9]$");

    // ---- monitors ----

    [Fact]
    public async Task Monitor_Crud_Pause_Resume()
    {
        var (c, _) = await NewUser();
        var id = await CreateMonitor(c, new { name = "Mon site", url = "https://example.com/health", method = "get", intervalMinutes = 3, expectedStatus = 204, keyword = "ok", timeoutSeconds = 7 });

        var got = await Json(await c.GetAsync($"/api/v1/monitors/{id}"));
        Assert.Equal("GET", got.GetProperty("method").GetString());
        Assert.Equal(3, got.GetProperty("intervalMinutes").GetInt32());
        Assert.Equal("Unknown", got.GetProperty("status").GetString());

        var upd = await c.PutAsJsonAsync($"/api/v1/monitors/{id}", new { name = "Renomme", url = "https://example.org", intervalMinutes = 5 });
        Assert.Equal("Renomme", (await Json(upd)).GetProperty("name").GetString());

        var paused = await Json(await c.PostAsync($"/api/v1/monitors/{id}/pause", null));
        Assert.False(paused.GetProperty("active").GetBoolean());
        Assert.Equal("Paused", paused.GetProperty("status").GetString());

        // A paused monitor is never scheduled.
        var due = await api.Service<IMonitorRepository>().GetDueAsync(DateTimeOffset.UtcNow);
        Assert.DoesNotContain(due, m => m.MonitorId == id);

        var resumed = await Json(await c.PostAsync($"/api/v1/monitors/{id}/resume", null));
        Assert.True(resumed.GetProperty("active").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/monitors/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/monitors/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1:8000/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.0.10/")]
    [InlineData("http://[::1]/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task Create_RejectsUnsafeUrls_WithProblemDetails(string url)
    {
        var (c, _) = await NewUser();
        var res = await c.PostAsJsonAsync("/api/v1/monitors", new { name = "x", url });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        Assert.True((await Json(res)).GetProperty("errors").TryGetProperty("url", out _));
    }

    [Theory]
    [InlineData("{\"name\":\"\",\"url\":\"https://example.com\"}", "name")]
    [InlineData("{\"name\":\"x\",\"url\":\"https://example.com\",\"intervalMinutes\":0}", "intervalMinutes")]
    [InlineData("{\"name\":\"x\",\"url\":\"https://example.com\",\"intervalMinutes\":6}", "intervalMinutes")]
    [InlineData("{\"name\":\"x\",\"url\":\"https://example.com\",\"expectedStatus\":99}", "expectedStatus")]
    [InlineData("{\"name\":\"x\",\"url\":\"https://example.com\",\"timeoutSeconds\":31}", "timeoutSeconds")]
    [InlineData("{\"name\":\"x\",\"url\":\"https://example.com\",\"method\":\"DELETE\"}", "method")]
    public async Task Create_ValidatesFields(string json, string field)
    {
        var (c, _) = await NewUser();
        var res = await c.PostAsync("/api/v1/monitors", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True((await Json(res)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Limit_Is20MonitorsPerAccount()
    {
        var (c, _) = await NewUser();
        for (var i = 0; i < 20; i++) await CreateMonitor(c, new { name = "m" + i, url = $"https://example.com/{i}" });
        var res = await c.PostAsJsonAsync("/api/v1/monitors", ValidMonitor);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(20, (await Json(await c.GetAsync("/api/v1/monitors"))).GetArrayLength());
    }

    [Fact]
    public async Task Accounts_AreIsolated()
    {
        var (a, _) = await NewUser();
        var (b, _) = await NewUser();
        var id = await CreateMonitor(a);
        var alertId = (await Json(await a.PostAsJsonAsync("/api/v1/alerts", new { type = "email", target = "x@example.com" }))).GetProperty("id").GetString();

        Assert.Equal(0, (await Json(await b.GetAsync("/api/v1/monitors"))).GetArrayLength());
        foreach (var path in new[] { "", "/stats", "/latency", "/availability", "/incidents" })
            Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/monitors/{id}{path}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/v1/monitors/{id}", ValidMonitor)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/v1/monitors/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/v1/monitors/{id}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/v1/alerts/{alertId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/v1/alerts/{alertId}/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/v1/monitors/{id}")).StatusCode); // still intact
    }

    // ---- stats ----

    [Fact]
    public async Task Stats_Latency_Availability_Incidents()
    {
        var (c, userId) = await NewUser();
        var id = await CreateMonitor(c);
        var now = DateTimeOffset.UtcNow;
        var results = api.Service<IResultRepository>();
        var aggs = api.Service<IAggregateRepository>();
        var incidents = api.Service<IIncidentRepository>();

        for (var i = 0; i < 9; i++)
        {
            await results.AddAsync(new CheckResult(id, now.AddMinutes(-i - 1), true, 200, 100 + i, ErrorKind.None, null));
            await aggs.RecordAsync(id, DateOnly.FromDateTime(now.UtcDateTime), true, 100 + i);
        }
        await results.AddAsync(new CheckResult(id, now.AddMinutes(-20), false, null, 5000, ErrorKind.Timeout, "t"));
        await aggs.RecordAsync(id, DateOnly.FromDateTime(now.UtcDateTime), false, 5000);
        await incidents.OpenAsync(new Incident(userId, id, Incident.NewId(now.AddMinutes(-20)), now.AddMinutes(-20), now.AddMinutes(-10), "Timeout"));

        var stats = await Json(await c.GetAsync($"/api/v1/monitors/{id}/stats"));
        Assert.Equal(90.0, stats.GetProperty("availability24h").GetDouble());
        Assert.Equal(90.0, stats.GetProperty("availability30d").GetDouble());
        Assert.Equal(10, stats.GetProperty("checks24h").GetInt32());
        Assert.InRange(stats.GetProperty("p95Ms24h").GetInt32(), 100, 120);

        Assert.True((await Json(await c.GetAsync($"/api/v1/monitors/{id}/latency?range=24h"))).GetArrayLength() >= 1);
        Assert.True((await Json(await c.GetAsync($"/api/v1/monitors/{id}/latency?range=7d"))).GetArrayLength() >= 1);

        var days = await Json(await c.GetAsync($"/api/v1/monitors/{id}/availability?days=90"));
        Assert.Equal(90, days.GetArrayLength());
        Assert.Equal(90.0, days[89].GetProperty("availability").GetDouble());
        Assert.Equal(JsonValueKind.Null, days[0].GetProperty("availability").ValueKind);

        var inc = Assert.Single((await Json(await c.GetAsync($"/api/v1/monitors/{id}/incidents"))).EnumerateArray());
        Assert.Equal(600, inc.GetProperty("durationSeconds").GetDouble());

        var list = await Json(await c.GetAsync("/api/v1/monitors"));
        Assert.Equal(90.0, list[0].GetProperty("availability30d").GetDouble());
    }

    // ---- alerts ----

    [Fact]
    public async Task Alerts_Crud_SecretShownOnce_AndTestButton()
    {
        var (c, _) = await NewUser();
        var created = await Json(await c.PostAsJsonAsync("/api/v1/alerts", new { type = "webhook", target = "https://hooks.example.com/x" }));
        var id = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("secret").GetString();
        Assert.False(string.IsNullOrEmpty(secret));

        var listed = (await Json(await c.GetAsync("/api/v1/alerts"))).EnumerateArray().First(a => a.GetProperty("id").GetString() == id);
        Assert.True(listed.GetProperty("hasSecret").GetBoolean());
        Assert.False(listed.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.String);

        var res = await c.PostAsync($"/api/v1/alerts/{id}/test", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var sent = Assert.Single(api.Senders.Hooks, h => h.Secret == secret);
        Assert.Contains("\"event\":\"test\"", sent.Body);

        api.Senders.FailHooks = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await c.PostAsync($"/api/v1/alerts/{id}/test", null)).StatusCode);
        api.Senders.FailHooks = false;

        var upd = await c.PutAsJsonAsync($"/api/v1/alerts/{id}", new { type = "webhook", target = "https://hooks.example.com/y", active = false });
        Assert.False((await Json(upd)).GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/alerts/{id}")).StatusCode);
    }

    [Fact]
    public async Task Alerts_EmailTest_SendsEmail()
    {
        var email = ApiFixture.NewEmail();
        var c = await api.ClientForAsync(email);
        var id = (await Json(await c.GetAsync("/api/v1/alerts"))).EnumerateArray().First().GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/v1/alerts/{id}/test", null)).StatusCode);
        Assert.Contains(api.Senders.Emails, e => e.To == email);
    }

    [Theory]
    [InlineData("email", "not-an-email")]
    [InlineData("webhook", "http://169.254.169.254/")]
    [InlineData("webhook", "ftp://x.io")]
    [InlineData("sms", "123")]
    public async Task Alerts_ValidateInput(string type, string target)
    {
        var (c, _) = await NewUser();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/alerts", new { type, target })).StatusCode);
    }

    // ---- slug & public status page ----

    [Fact]
    public async Task Slug_Change_Conflict_AndValidation()
    {
        var (a, _) = await NewUser();
        var (b, _) = await NewUser();
        var wanted = "co-" + Guid.NewGuid().ToString("N")[..10];
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsJsonAsync("/api/v1/me/slug", new { slug = wanted })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await b.PutAsJsonAsync("/api/v1/me/slug", new { slug = wanted })).StatusCode);
        foreach (var bad in new[] { "A", "ab", "UPPER", "api", "with space", "-start", "a--b", new string('a', 40) })
            Assert.Equal(HttpStatusCode.BadRequest, (await b.PutAsJsonAsync("/api/v1/me/slug", new { slug = bad })).StatusCode);
    }

    [Fact]
    public async Task PublicStatusPage_IsAnonymous_HidesUrls_AndIsCached()
    {
        var (c, userId) = await NewUser();
        var slug = "pg-" + Guid.NewGuid().ToString("N")[..10];
        await c.PutAsJsonAsync("/api/v1/me/slug", new { slug });
        var up = await CreateMonitor(c, new { name = "Front", url = "https://secret-internal-name.example.com" });
        var down = await CreateMonitor(c, new { name = "API", url = "https://example.com/api" });
        var paused = await CreateMonitor(c, new { name = "Hidden", url = "https://example.com/p" });
        await c.PostAsync($"/api/v1/monitors/{paused}/pause", null);

        var monitors = api.Service<IMonitorRepository>();
        await monitors.UpdateAsync((await monitors.GetAsync(userId, up))! with { Status = MonitorStatus.Up });
        await monitors.UpdateAsync((await monitors.GetAsync(userId, down))! with { Status = MonitorStatus.Down });
        await api.Service<IIncidentRepository>().OpenAsync(new Incident(userId, down, Incident.NewId(DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, null, "Timeout"));

        var anon = api.CreateClient();
        var res = await anon.GetAsync($"/api/v1/status/{slug}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("max-age=30", res.Headers.CacheControl!.ToString());
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-internal-name", body);
        Assert.DoesNotContain("Hidden", body);
        var page = JsonDocument.Parse(body).RootElement;
        Assert.Equal("partial_outage", page.GetProperty("overall").GetString());
        Assert.Equal(2, page.GetProperty("services").GetArrayLength());
        Assert.Equal(90, page.GetProperty("services")[0].GetProperty("days").GetArrayLength());
        Assert.Equal("Timeout", page.GetProperty("incidents")[0].GetProperty("cause").GetString());

        // Cached for 30 s: a state change is not visible immediately.
        await monitors.UpdateAsync((await monitors.GetAsync(userId, down))! with { Status = MonitorStatus.Up });
        var again = JsonDocument.Parse(await (await anon.GetAsync($"/api/v1/status/{slug}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("partial_outage", again.GetProperty("overall").GetString());
    }

    [Fact]
    public async Task PublicStatusPage_UnknownSlug_Is404()
    {
        var anon = api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/status/does-not-exist-123")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/status/BAD_SLUG!")).StatusCode);
    }

    // ---- demo target ----

    [Fact]
    public async Task DemoTarget_FollowsForcedState()
    {
        var (c, _) = await NewUser();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/demo", new { mode = "explode" })).StatusCode);

        await c.PutAsJsonAsync("/api/v1/demo", new { mode = "down" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await api.CreateClient().GetAsync("/demo/target")).StatusCode);
        Assert.Equal("down", (await Json(await c.GetAsync("/api/v1/demo"))).GetProperty("mode").GetString());

        await c.PutAsJsonAsync("/api/v1/demo", new { mode = "ok" });
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/demo/target")).StatusCode);
    }

    [Fact]
    public async Task Demo_RequiresAuthentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PutAsJsonAsync("/api/v1/demo", new { mode = "down" })).StatusCode);

    // ---- misc ----

    [Fact]
    public async Task OpenApi_IsPublished()
    {
        var res = await api.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("/api/v1/monitors", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetails()
    {
        var res = await (await api.ClientForAsync(ApiFixture.NewEmail())).GetAsync("/api/v1/nothing");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}

[Collection("dynamo")]
public class RateLimitTests(DynamoFixture fx)
{
    [Fact]
    public async Task ApiPolicy_Returns429_WhenExceeded()
    {
        var api = new ApiFixture(fx);
        api.Overrides["RateLimit:ApiPerMinute"] = "5";
        await api.InitializeAsync();
        var c = await api.ClientForAsync(ApiFixture.NewEmail());
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) codes.Add((await c.GetAsync("/api/v1/monitors")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.OK, codes[0]);
        await api.DisposeAsync();
    }

    [Fact]
    public async Task PublicPolicy_Returns429_WhenExceeded()
    {
        var api = new ApiFixture(fx);
        api.Overrides["RateLimit:PublicPerMinute"] = "3";
        await api.InitializeAsync();
        var c = api.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) codes.Add((await c.GetAsync("/api/v1/status/nothing-here")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        await api.DisposeAsync();
    }
}

