using Microsoft.AspNetCore.Mvc.Testing;

namespace StatusHub.IntegrationTests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        var res = await factory.WithWebHostBuilder(b => b.UseSetting("StatusHub:LocalWorker", "false")).CreateClient().GetAsync("/health");
        Assert.True(res.IsSuccessStatusCode);
    }
}

