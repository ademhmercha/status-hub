using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StatusHub.Application;
using StatusHub.Infrastructure.Dynamo;

namespace StatusHub.IntegrationTests;

public sealed class FakeSenders
{
    public List<(string To, string Subject)> Emails { get; } = [];
    public List<(string Url, string? Secret, string Body)> Hooks { get; } = [];
    public bool FailHooks { get; set; }
    public IEmailSender Email { get; }
    public IWebhookSender Webhook { get; }

    public FakeSenders()
    {
        Email = new EmailImpl(this);
        Webhook = new HookImpl(this);
    }

    private sealed class EmailImpl(FakeSenders o) : IEmailSender
    {
        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct) { o.Emails.Add((to, subject)); return Task.CompletedTask; }
    }

    private sealed class HookImpl(FakeSenders o) : IWebhookSender
    {
        public Task SendAsync(string url, string? secret, string jsonBody, CancellationToken ct)
        {
            if (o.FailHooks) throw new HttpRequestException("receiver returned 500");
            o.Hooks.Add((url, secret, jsonBody));
            return Task.CompletedTask;
        }
    }
}

/// <summary>The real API (WebApplicationFactory) on top of a fresh DynamoDB Local table.</summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly DynamoFixture _dynamo;
    private string _table = "";
    public FakeSenders Senders { get; } = new();
    public Dictionary<string, string?> Overrides { get; } = new()
    {
        ["StatusHub:LocalWorker"] = "false",
        ["Checker:AllowPrivateTargets"] = "false",
        ["Demo:Enabled"] = "true",
        ["Auth:DevTokens"] = "true",
        ["Auth:DevSigningKey"] = "integration-test-signing-key-0123456789abcdef",
        ["RateLimit:ApiPerMinute"] = "100000",
        ["RateLimit:TokenPerMinute"] = "100000",
        ["RateLimit:DemoPerMinute"] = "100000",
        ["RateLimit:PublicPerMinute"] = "100000",
    };

    public ApiFixture(DynamoFixture dynamo) => _dynamo = dynamo;

    public async Task InitializeAsync()
    {
        var (_, table) = await _dynamo.NewTableAsync();
        _table = table;
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DynamoDb:ServiceUrl", _dynamo.ServiceUrl);
        builder.UseSetting("DynamoDb:TableName", _table);
        foreach (var (k, v) in Overrides) builder.UseSetting(k, v);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IEmailSender>();
            s.RemoveAll<IWebhookSender>();
            s.AddSingleton(Senders.Email);
            s.AddSingleton(Senders.Webhook);
        });
    }

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<HttpClient> ClientForAsync(string email)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/dev/token", new { email });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["token"];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static string NewEmail() => $"u{Guid.NewGuid():N}@example.com";
}




