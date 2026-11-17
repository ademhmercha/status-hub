using Amazon;
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
        return s;
    }
}
