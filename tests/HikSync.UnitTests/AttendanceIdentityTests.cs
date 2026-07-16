using FluentAssertions;
using HikSync.Core.Logic;

namespace HikSync.UnitTests;

public class AttendanceIdentityTests
{
    [Fact]
    public void ComputeKey_HasExpectedShape()
    {
        var t = new DateTime(2026, 7, 16, 8, 31, 22, DateTimeKind.Utc);
        var key = AttendanceIdentity.ComputeKey("192.168.1.10", "1042", t, 5, 75);

        long unix = new DateTimeOffset(t).ToUnixTimeSeconds();
        key.Should().Be($"192.168.1.10:1042:{unix}:5:75");
    }

    [Fact]
    public void ComputeKey_IsIndependentOfDeviceSerial()
    {
        // Identity does not include the serial, so a device event-log reset (serial restarts low)
        // cannot make a genuinely-new event collide with an old one.
        var t = new DateTime(2026, 7, 16, 8, 31, 22, DateTimeKind.Utc);
        var a = AttendanceIdentity.ComputeKey("10.0.0.1", "1042", t, 5, 75);
        var b = AttendanceIdentity.ComputeKey("10.0.0.1", "1042", t, 5, 75);
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeKey_DiffersByTime()
    {
        var t1 = new DateTime(2026, 7, 16, 8, 31, 22, DateTimeKind.Utc);
        var t2 = t1.AddSeconds(1);
        AttendanceIdentity.ComputeKey("10.0.0.1", "1042", t1, 5, 75)
            .Should().NotBe(AttendanceIdentity.ComputeKey("10.0.0.1", "1042", t2, 5, 75));
    }
}
