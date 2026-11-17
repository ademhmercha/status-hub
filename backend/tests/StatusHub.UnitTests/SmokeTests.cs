using StatusHub.Domain;

namespace StatusHub.UnitTests;

public class MessageTests
{
    [Fact]
    public void IdempotencyKey_IsMonitorPlusMinute()
    {
        var m = new CheckMessage("mon1", new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero));
        Assert.Equal("mon1#202601020304", m.IdempotencyKey);
    }
}
