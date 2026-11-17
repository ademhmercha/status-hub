using System.Net;
using StatusHub.Domain;

namespace StatusHub.UnitTests;

public class IncidentPolicyTests
{
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static UptimeMonitor M(MonitorStatus s = MonitorStatus.Unknown, int f = 0, int ok = 0, string? incident = null) =>
        new("a", "m", "n", "https://x.io", "GET", 1, 200, null, 10, true, T, s, f, ok, incident);

    private static UptimeMonitor Run(UptimeMonitor m, params bool[] results)
    {
        foreach (var r in results) m = IncidentPolicy.Apply(m, r, T).Monitor;
        return m;
    }

    [Fact]
    public void SingleFailure_DoesNotOpenIncident()
    {
        var (m, t) = IncidentPolicy.Apply(M(), false, T);
        Assert.Equal(Transition.None, t);
        Assert.Equal(1, m.ConsecutiveFailures);
        Assert.Equal(MonitorStatus.Unknown, m.Status);
    }

    [Fact]
    public void TwoConsecutiveFailures_OpenIncident()
    {
        var (first, _) = IncidentPolicy.Apply(M(), false, T);
        var (m, t) = IncidentPolicy.Apply(first, false, T);
        Assert.Equal(Transition.Opened, t);
        Assert.Equal(MonitorStatus.Down, m.Status);
    }

    [Fact]
    public void FailureSuccessFailure_DoesNotOpen()
    {
        var m = Run(M(), false, true, false);
        Assert.Null(m.OpenIncidentId);
        Assert.NotEqual(MonitorStatus.Down, m.Status);
        Assert.Equal(1, m.ConsecutiveFailures);
    }

    [Fact]
    public void ThirdFailure_WhileIncidentOpen_DoesNotOpenAgain()
    {
        var (m, t) = IncidentPolicy.Apply(M(MonitorStatus.Down, 2, 0, "inc1"), false, T);
        Assert.Equal(Transition.None, t);
        Assert.Equal(3, m.ConsecutiveFailures);
        Assert.Equal(MonitorStatus.Down, m.Status);
    }

    [Fact]
    public void OneSuccess_WhileDown_StaysDown()
    {
        var (m, t) = IncidentPolicy.Apply(M(MonitorStatus.Down, 2, 0, "inc1"), true, T);
        Assert.Equal(Transition.None, t);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Equal("inc1", m.OpenIncidentId);
    }

    [Fact]
    public void TwoSuccesses_WhileDown_Resolve()
    {
        var m1 = IncidentPolicy.Apply(M(MonitorStatus.Down, 2, 0, "inc1"), true, T).Monitor;
        var (m, t) = IncidentPolicy.Apply(m1, true, T);
        Assert.Equal(Transition.Resolved, t);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Null(m.OpenIncidentId);
    }

    [Fact]
    public void SuccessFailureSuccess_WhileDown_DoesNotResolve()
    {
        var m = Run(M(MonitorStatus.Down, 2, 0, "inc1"), true, false, true);
        Assert.Equal("inc1", m.OpenIncidentId);
        Assert.Equal(MonitorStatus.Down, m.Status);
    }

    [Fact]
    public void FirstSuccess_FromUnknown_MarksUp()
    {
        var (m, t) = IncidentPolicy.Apply(M(), true, T);
        Assert.Equal(Transition.None, t);
        Assert.Equal(MonitorStatus.Up, m.Status);
    }

    [Fact]
    public void FullCycle_OpensOnceAndResolvesOnce()
    {
        var m = M();
        var transitions = new List<Transition>();
        foreach (var r in new[] { true, true, false, false, false, false, true, true, true, false })
        {
            var (next, t) = IncidentPolicy.Apply(m, r, T);
            m = t == Transition.Opened ? next with { OpenIncidentId = "i" } : next;
            if (t != Transition.None) transitions.Add(t);
        }
        Assert.Equal([Transition.Opened, Transition.Resolved], transitions);
        Assert.Equal(1, m.ConsecutiveFailures);
    }

    [Fact]
    public void SetsLastCheckedAt()
    {
        var at = T.AddMinutes(5);
        Assert.Equal(at, IncidentPolicy.Apply(M(), true, at).Monitor.LastCheckedAt);
    }
}

public class UrlGuardTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("https://8.8.8.8/")]
    [InlineData("https://sub.domain.example.co.uk:8443/x")]
    public void Allows_PublicUrls(string url) => Assert.True(UrlGuard.TryValidate(url, false, out _));

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://localhost")]
    [InlineData("http://LOCALHOST:5000/")]
    [InlineData("http://foo.localhost/")]
    [InlineData("http://intranet/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://db.internal/")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.1.2.3")]
    [InlineData("http://10.0.0.5")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://172.31.255.255")]
    [InlineData("http://192.168.1.1")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://100.64.0.1")]
    [InlineData("http://0.0.0.0")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("https://user:pass@example.com")]
    public void Rejects_UnsafeUrls(string url) => Assert.False(UrlGuard.TryValidate(url, false, out var err) && err is null);

    [Fact]
    public void AllowPrivate_PermitsLocalhost() => Assert.True(UrlGuard.TryValidate("http://localhost:5000/demo/target", true, out _));

    [Theory]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.20.0.1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsPrivateAddress(string ip, bool expected) => Assert.Equal(expected, UrlGuard.IsPrivateAddress(IPAddress.Parse(ip)));
}

public class WebhookSignerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Signature_IsStableAndPrefixed()
    {
        var s1 = WebhookSigner.Sign("secret", 1000, "{}");
        Assert.Equal(s1, WebhookSigner.Sign("secret", 1000, "{}"));
        Assert.StartsWith("sha256=", s1);
        Assert.Equal(7 + 64, s1.Length);
    }

    [Fact]
    public void Signature_DependsOnSecretTimestampAndBody()
    {
        var s = WebhookSigner.Sign("secret", 1000, "{}");
        Assert.NotEqual(s, WebhookSigner.Sign("other", 1000, "{}"));
        Assert.NotEqual(s, WebhookSigner.Sign("secret", 1001, "{}"));
        Assert.NotEqual(s, WebhookSigner.Sign("secret", 1000, "{ }"));
    }

    [Fact]
    public void Verify_AcceptsFreshValidSignature()
    {
        var ts = Now.ToUnixTimeSeconds();
        Assert.True(WebhookSigner.Verify("k", ts, "body", WebhookSigner.Sign("k", ts, "body"), Now));
    }

    [Fact]
    public void Verify_RejectsTamperedBody()
    {
        var ts = Now.ToUnixTimeSeconds();
        Assert.False(WebhookSigner.Verify("k", ts, "bodyX", WebhookSigner.Sign("k", ts, "body"), Now));
    }

    [Fact]
    public void Verify_RejectsReplayOutsideTolerance()
    {
        var ts = Now.AddMinutes(-6).ToUnixTimeSeconds();
        Assert.False(WebhookSigner.Verify("k", ts, "body", WebhookSigner.Sign("k", ts, "body"), Now));
        var future = Now.AddMinutes(6).ToUnixTimeSeconds();
        Assert.False(WebhookSigner.Verify("k", future, "body", WebhookSigner.Sign("k", future, "body"), Now));
    }

    [Fact]
    public void Verify_AcceptsWithinTolerance()
    {
        var ts = Now.AddMinutes(-4).ToUnixTimeSeconds();
        Assert.True(WebhookSigner.Verify("k", ts, "body", WebhookSigner.Sign("k", ts, "body"), Now));
    }
}

public class DailyAggregateTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0)]
    [InlineData(51, 1)]
    [InlineData(10000, 11)]
    [InlineData(10001, 12)]
    public void BucketFor(int latency, int bucket) => Assert.Equal(bucket, DailyAggregate.BucketFor(latency));

    [Fact]
    public void Empty_HasFullAvailabilityAndZeroPercentiles()
    {
        var a = DailyAggregate.Empty("m", new DateOnly(2026, 1, 1));
        Assert.Equal(100, a.Availability);
        Assert.Equal(0, a.P95);
    }

    [Fact]
    public void Percentiles_ReadFromHistogram()
    {
        var h = new int[DailyAggregate.BucketCount];
        h[1] = 90; // <=100 ms
        h[6] = 10; // <=1000 ms
        var a = new DailyAggregate("m", new DateOnly(2026, 1, 1), 100, 100, 0, h);
        Assert.Equal(100, a.P50);
        Assert.Equal(1000, a.P95);
    }

    [Fact]
    public void DueKeys_CoverEachIntervalOncePerWindow()
    {
        var minute = new DateTimeOffset(2026, 1, 1, 0, 7, 0, TimeSpan.Zero);
        Assert.Equal(5, UptimeMonitor.DueKeysAt(minute).Count());
        var m = new UptimeMonitor("a", "m-1", "n", "https://x.io", "GET", 3, 200, null, 10, true, minute);
        var hits = Enumerable.Range(0, 3).Count(k => UptimeMonitor.DueKeysAt(minute.AddMinutes(k)).Contains(m.DueKey));
        Assert.Equal(1, hits);
    }
}
