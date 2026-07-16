using FluentAssertions;
using HikSync.Application;
using HikSync.Core.Configuration;
using HikSync.Core.Models;
using HikSync.Device.Fake;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HikSync.UnitTests;

public class AttendanceCollectorTests
{
    private static readonly DateTime Base = new(2026, 7, 16, 8, 0, 0, DateTimeKind.Utc);

    private static AccessEvent Event(long serial, DateTime timeUtc, VerifyMode mode = VerifyMode.Fingerprint) => new()
    {
        SerialNo = serial,
        EventTimeUtc = timeUtc,
        EmployeeNo = "1001",
        Major = 5,
        Minor = 75,
        VerifyMode = mode,
        Raw = "{}",
    };

    private sealed record Harness(
        AttendanceCollector Collector,
        FakeAccessDevice InDevice,
        InMemoryAttendanceRepository Attendance,
        InMemoryWatermarkRepository Watermarks,
        InMemoryOperationLogRepository Log);

    private static Harness BuildHarness(string[]? countedModes = null)
    {
        var inEp = new DeviceEndpoint { Ip = "10.0.0.1", Port = 8000 };
        var outEp = new DeviceEndpoint { Ip = "10.0.0.2", Port = 8001 };

        var factory = new FakeAccessDeviceFactory();
        var info = new DeviceInfo { Model = "DS-K1A8503MF-B" };
        var inDevice = factory.Register(new FakeAccessDevice(inEp, info));
        factory.Register(new FakeAccessDevice(outEp, info)); // OUT starts empty

        var pair = new DevicePair { Id = 1, Location = "Main Gate", In = inEp, Out = outEp };

        var attendance = new InMemoryAttendanceRepository();
        var watermarks = new InMemoryWatermarkRepository();
        var logRepo = new InMemoryOperationLogRepository();
        var options = Options.Create(new AttendanceOptions
        {
            BackfillStartUtc = Base.AddHours(-1),
            MaxConcurrentDevices = 2,
            CountedVerifyModes = countedModes ?? Array.Empty<string>(),
        });

        var collector = new AttendanceCollector(
            new InMemoryDevicePairRepository(pair),
            watermarks,
            attendance,
            factory,
            new OperationLogger(logRepo, NullLogger<OperationLogger>.Instance),
            options,
            new HealthState(),
            NullLogger<AttendanceCollector>.Instance);

        return new Harness(collector, inDevice, attendance, watermarks, logRepo);
    }

    [Fact]
    public async Task CapturesNewEvents_TaggedByRole()
    {
        var h = BuildHarness();
        h.InDevice.SeedEvents(new[]
        {
            Event(10, Base.AddMinutes(1)),
            Event(11, Base.AddMinutes(2)),
            Event(12, Base.AddMinutes(3)),
        });

        await h.Collector.CollectAllAsync(CancellationToken.None);

        h.Attendance.Rows.Should().HaveCount(3);
        h.Attendance.Rows.Should().OnlyContain(r => r.Role == DeviceRole.In);
        h.Attendance.Rows.Should().OnlyContain(r => r.Location == "Main Gate");
        h.Watermarks.Store["10.0.0.1"].LastEventTimeUtc.Should().Be(Base.AddMinutes(3));
    }

    [Fact]
    public async Task ReRun_CapturesNoDuplicates()
    {
        var h = BuildHarness();
        h.InDevice.SeedEvents(new[] { Event(10, Base.AddMinutes(1)), Event(11, Base.AddMinutes(2)) });

        await h.Collector.CollectAllAsync(CancellationToken.None);
        await h.Collector.CollectAllAsync(CancellationToken.None); // window overlaps; dedup must absorb it

        h.Attendance.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task NewEventAfterReRun_IsCaptured()
    {
        var h = BuildHarness();
        h.InDevice.SeedEvents(new[] { Event(10, Base.AddMinutes(1)) });
        await h.Collector.CollectAllAsync(CancellationToken.None);

        h.InDevice.SeedEvents(new[] { Event(11, Base.AddMinutes(5)) });
        await h.Collector.CollectAllAsync(CancellationToken.None);

        h.Attendance.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task SerialReset_DoesNotDropNewEvent()
    {
        var h = BuildHarness();
        h.InDevice.SeedEvents(new[] { Event(5000, Base.AddMinutes(1)) });
        await h.Collector.CollectAllAsync(CancellationToken.None);

        // Device event-log wiped: serial restarts low, but the event is genuinely new (later time).
        h.InDevice.SeedEvents(new[] { Event(1, Base.AddMinutes(10)) });
        await h.Collector.CollectAllAsync(CancellationToken.None);

        h.Attendance.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task LogsConnectOperationDisconnect_TaggedWithIpAndRole()
    {
        var h = BuildHarness();
        h.InDevice.SeedEvents(new[] { Event(10, Base.AddMinutes(1)) });

        await h.Collector.CollectAllAsync(CancellationToken.None);

        var inLogs = h.Log.Entries.Where(e => e.DeviceIp == "10.0.0.1").ToList();
        inLogs.Should().OnlyContain(e => e.Role == DeviceRole.In);
        inLogs.Should().Contain(e => e.Operation == "connect" && e.Status == "ok");
        inLogs.Should().Contain(e => e.Operation == "attendance");
        inLogs.Should().Contain(e => e.Operation == "disconnect");

        // The OUT terminal session is recorded too, tagged OUT.
        h.Log.Entries.Should().Contain(e => e.DeviceIp == "10.0.0.2" && e.Role == DeviceRole.Out && e.Operation == "disconnect");
    }

    [Fact]
    public async Task VerifyModeFilter_ExcludesUncountedModes()
    {
        var h = BuildHarness(countedModes: new[] { "Fingerprint" });
        h.InDevice.SeedEvents(new[]
        {
            Event(10, Base.AddMinutes(1), VerifyMode.Fingerprint),
            Event(11, Base.AddMinutes(2), VerifyMode.Card),
        });

        await h.Collector.CollectAllAsync(CancellationToken.None);

        h.Attendance.Rows.Should().ContainSingle();
        h.Attendance.Rows[0].VerifyMode.Should().Be("Fingerprint");
    }
}
