using Amazon.DynamoDBv2;
using Amazon.Runtime;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StatusHub.Infrastructure.Dynamo;

namespace StatusHub.IntegrationTests;

/// <summary>One DynamoDB Local container for the whole test run; each test gets its own table.</summary>
public sealed class DynamoFixture : IAsyncLifetime
{
    private IContainer? _container;
    public string ServiceUrl { get; private set; } = "";
    public IAmazonDynamoDB Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("amazon/dynamodb-local:latest")
            .WithPortBinding(8000, true)
            .WithCommand("-jar", "DynamoDBLocal.jar", "-inMemory", "-sharedDb")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Initializing DynamoDB Local"))
            .Build();
        await _container.StartAsync();
        ServiceUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8000)}";
        Client = new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"),
            new AmazonDynamoDBConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "eu-north-1" });
    }

    public async Task<(DynamoOptions Options, string Table)> NewTableAsync()
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        await TableBootstrapper.EnsureAsync(Client, name);
        return (new DynamoOptions(name), name);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition("dynamo")]
public sealed class DynamoCollection : ICollectionFixture<DynamoFixture>;
