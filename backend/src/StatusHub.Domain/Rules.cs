using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace StatusHub.Domain;

public enum Transition { None, Opened, Resolved }

/// <summary>Incident state machine: opens after 2 consecutive failures, closes after 2 consecutive successes.</summary>
public static class IncidentPolicy
{
    public const int FailuresToOpen = 2;
    public const int SuccessesToClose = 2;

    public static (UptimeMonitor Monitor, Transition Transition) Apply(UptimeMonitor m, bool success, DateTimeOffset at)
    {
        if (!success)
        {
            var failures = m.ConsecutiveFailures + 1;
            var next = m with { ConsecutiveFailures = failures, ConsecutiveSuccesses = 0, LastCheckedAt = at };
            if (failures >= FailuresToOpen && m.OpenIncidentId is null)
                return (next with { Status = MonitorStatus.Down }, Transition.Opened);
            return (next, Transition.None);
        }

        var successes = m.ConsecutiveSuccesses + 1;
        var ok = m with { ConsecutiveSuccesses = successes, ConsecutiveFailures = 0, LastCheckedAt = at };
        if (m.OpenIncidentId is not null)
        {
            // Still Down until enough successes in a row.
            return successes >= SuccessesToClose
                ? (ok with { Status = MonitorStatus.Up, OpenIncidentId = null }, Transition.Resolved)
                : (ok, Transition.None);
        }
        return (ok with { Status = MonitorStatus.Up }, Transition.None);
    }
}

/// <summary>SSRF protection: http/https only, no localhost, no private/link-local addresses.</summary>
public static class UrlGuard
{
    public static bool TryValidate(string? url, bool allowPrivate, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "URL invalide.";
            return false;
        }
        if (uri.Scheme is not ("http" or "https"))
        {
            error = "Seuls http et https sont autorises.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Les identifiants dans l'URL ne sont pas autorises.";
            return false;
        }
        if (allowPrivate) return true;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal) ||
            !host.Contains('.') && !IPAddress.TryParse(host, out _))
        {
            error = "Les hotes locaux ne sont pas autorises.";
            return false;
        }
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsPrivateAddress(ip))
        {
            error = "Les adresses IP privees ne sont pas autorisees.";
            return false;
        }
        return true;
    }

    public static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 0 || b[0] == 10 ||
                   (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                   b[0] >= 224; // multicast / reserved / broadcast
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC;
        return true;
    }
}

/// <summary>HMAC-SHA256 webhook signature. Signed content is "{timestamp}.{body}" so a captured request cannot be replayed later.</summary>
public static class WebhookSigner
{
    public const string SignatureHeader = "X-StatusHub-Signature";
    public const string TimestampHeader = "X-StatusHub-Timestamp";
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, long unixTimestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{unixTimestamp}.{body}"));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    public static bool Verify(string secret, long unixTimestamp, string body, string signature, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        var age = now.ToUnixTimeSeconds() - unixTimestamp;
        if (Math.Abs(age) > (tolerance ?? DefaultTolerance).TotalSeconds) return false;
        var expected = Encoding.UTF8.GetBytes(Sign(secret, unixTimestamp, body));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(signature));
    }
}
