using StatusHub.Domain;

namespace StatusHub.UnitTests;

public class MessageTests
{
    [Fact]
    public void IdempotencyKey_IsMonitorPlusMinute()
    {
        var m = new CheckMessage("a", "mon1", new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero), "https://x", "GET", 200, null, 10);
        Assert.Equal("mon1#202601020304", m.IdempotencyKey);
    }
}

