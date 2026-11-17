using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Infrastructure;

public static class SafeHttp
{
    /// <summary>
    /// HttpClient handler that resolves DNS itself and refuses private/loopback addresses (SSRF and DNS-rebinding protection,
    /// including after redirects). Pass allowPrivate=true only for local development.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
            if (!allowPrivate && addresses.Any(UrlGuard.IsPrivateAddress))
                throw new HttpRequestException("Target resolves to a private address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>Local development: MailHog (SMTP on 1025).</summary>
public sealed class SmtpEmailSender(string host, int port, string from) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string textBody, CancellationToken ct)
    {
        using var client = new SmtpClient(host, port);
        using var msg = new MailMessage(from, to, subject, textBody) { BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
        await client.SendMailAsync(msg, ct);
    }
}

public sealed class SesEmailSender(IAmazonSimpleEmailServiceV2 ses, string from) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string textBody, CancellationToken ct) =>
        await ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = from,
            Destination = new Destination { ToAddresses = [to] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = subject, Charset = "UTF-8" },
                    Body = new Body { Text = new Content { Data = textBody, Charset = "UTF-8" } },
                },
            },
        }, ct);
}

public sealed class WebhookSender(HttpClient http, TimeProvider clock) : IWebhookSender
{
    public async Task SendAsync(string url, string? secret, string jsonBody, CancellationToken ct)
    {
        if (!UrlGuard.TryValidate(url, allowPrivate: true, out var error)) throw new InvalidOperationException(error);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        req.Headers.UserAgent.ParseAdd("StatusHub-Webhook/1.0");
        if (!string.IsNullOrEmpty(secret))
        {
            var ts = clock.GetUtcNow().ToUnixTimeSeconds();
            req.Headers.Add(WebhookSigner.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            req.Headers.Add(WebhookSigner.SignatureHeader, WebhookSigner.Sign(secret, ts, jsonBody));
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var res = await http.SendAsync(req, cts.Token);
        res.EnsureSuccessStatusCode();
    }
}
