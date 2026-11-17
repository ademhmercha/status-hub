using Amazon.EventBridge;
using Amazon.SimpleEmailV2;
using Amazon.SQS;
using AWS.Lambda.Powertools.Logging;
using Microsoft.Extensions.Logging;
using StatusHub.Domain;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StatusHub.Application;
using StatusHub.Infrastructure.Dynamo;

namespace StatusHub.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Config: DynamoDb:TableName, DynamoDb:ServiceUrl (set for DynamoDB Local; leave empty on AWS).</summary>
    public static IServiceCollection AddStatusHubDynamo(this IServiceCollection s, IConfiguration config)
    {
        var table = config["DynamoDb:TableName"] ?? "statushub";
        var url = config["DynamoDb:ServiceUrl"];
        s.AddSingleton(new DynamoOptions(table));
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<IAmazonDynamoDB>(_ =>
        {
            if (string.IsNullOrEmpty(url)) return new AmazonDynamoDBClient();
            return new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"),
                new AmazonDynamoDBConfig { ServiceURL = url, AuthenticationRegion = "eu-north-1" });
        });
        s.AddSingleton<IAccountRepository, AccountRepository>();
        s.AddSingleton<IMonitorRepository, MonitorRepository>();
        s.AddSingleton<IResultRepository, ResultRepository>();
        s.AddSingleton<IIncidentRepository, IncidentRepository>();
        s.AddSingleton<IAggregateRepository, AggregateRepository>();
        s.AddSingleton<IAlertRepository, AlertRepository>();
        s.AddSingleton<IIdempotencyStore, IdempotencyStore>();
        s.AddSingleton<IDemoStateStore, DemoStateStore>();
        return s;
    }

    /// <summary>Shared HttpClient (SSRF-safe handler) + checker options. Checker:AllowPrivateTargets is for local development only.</summary>
    public static IServiceCollection AddStatusHubHttp(this IServiceCollection s, IConfiguration config)
    {
        var allowPrivate = config.GetValue("Checker:AllowPrivateTargets");
        s.AddSingleton(new CheckerOptions(allowPrivate));
        s.AddSingleton(_ => new HttpClient(SafeHttp.CreateHandler(allowPrivate), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan });
        return s;
    }

    /// <summary>Email provider: Email:Provider = Smtp (MailHog) or Ses. Email:From is the sender address.</summary>
    public static IServiceCollection AddStatusHubNotifications(this IServiceCollection s, IConfiguration config)
    {
        var from = config["Email:From"] ?? "alerts@statushub.local";
        if (string.Equals(config["Email:Provider"], "Ses", StringComparison.OrdinalIgnoreCase))
            s.AddSingleton<IEmailSender>(_ => new SesEmailSender(new AmazonSimpleEmailServiceV2Client(), from));
        else
            s.AddSingleton<IEmailSender>(_ => new SmtpEmailSender(config["Email:SmtpHost"] ?? "localhost", int.Parse(config["Email:SmtpPort"] ?? "1025", System.Globalization.CultureInfo.InvariantCulture), from));
        s.AddSingleton<IWebhookSender, WebhookSender>();
        s.AddSingleton(config.GetValue("Notifier:NoRetryDelay") ? new NotifierOptions([]) : NotifierOptions.Default);
        s.AddSingleton<AlertDispatcher>();
        return s;
    }

    /// <summary>SQS publishers for checks/results and the EventBridge publisher for incident events.</summary>
    public static IServiceCollection AddStatusHubAwsPublishers(this IServiceCollection s, IConfiguration config)
    {
        s.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient());
        s.AddSingleton<IAmazonEventBridge>(_ => new AmazonEventBridgeClient());
        s.AddSingleton<IMessagePublisher<CheckMessage>>(p => new SqsPublisher<CheckMessage>(p.GetRequiredService<IAmazonSQS>(), config["Queues:Checks"]!, StatusHubJson.Default.CheckMessage));
        s.AddSingleton<IMessagePublisher<CheckResultMessage>>(p => new SqsPublisher<CheckResultMessage>(p.GetRequiredService<IAmazonSQS>(), config["Queues:Results"]!, StatusHubJson.Default.CheckResultMessage));
        s.AddSingleton<IMessagePublisher<IncidentEvent>>(p => new EventBridgeIncidentPublisher(p.GetRequiredService<IAmazonEventBridge>(), config["EventBus:Name"]!));
        return s;
    }

    public static bool GetValue(this IConfiguration c, string key) => bool.TryParse(c[key], out var v) && v;

    /// <summary>Builds the container of a Lambda: configuration from environment variables (Section__Key), logging, stages.</summary>
    public static ServiceProvider BuildLambdaServices(Action<IServiceCollection, IConfiguration> configure)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var s = new ServiceCollection();
        s.AddLogging(b => b.AddPowertoolsLogger(c => { }));
        s.AddSingleton<IStatusMetrics, PowertoolsStatusMetrics>();
        s.AddStatusHubStages();
        configure(s, config);
        return s.BuildServiceProvider();
    }
}



