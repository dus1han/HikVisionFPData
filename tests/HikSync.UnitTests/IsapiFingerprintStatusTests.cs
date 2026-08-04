using System.Text.Json;
using FluentAssertions;
using HikSync.Device.Isapi;

namespace HikSync.UnitTests;

/// <summary>
/// DS-K1A8503MF-B answers HTTP 200 to a fingerprint apply whether or not it stored anything; the
/// real verdict is cardReaderRecvStatus. Treating 200 as success is what let rejected templates be
/// reported as synced.
/// </summary>
public class IsapiFingerprintStatusTests
{
    private static IsapiFingerprintStatus? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return IsapiFingerprintStatus.Parse(doc.RootElement);
    }

    [Fact]
    public void Stored_IsAccepted()
    {
        var status = Parse("""{"FingerPrintStatus":{"StatusList":[{"id":1,"cardReaderRecvStatus":1}]}}""");
        status.Should().NotBeNull();
        status!.Accepted.Should().BeTrue();
    }

    [Fact]
    public void AlreadyEnrolled_IsRejected_AndNamesTheOwner()
    {
        var status = Parse("""{"FingerPrintStatus":{"StatusList":[{"id":1,"cardReaderRecvStatus":5,"errorMsg":"692"}]}}""");

        status.Should().NotBeNull();
        status!.Accepted.Should().BeFalse();
        status.RecvStatus.Should().Be(5);
        status.ErrorMessage.Should().Be("692");
        status.Describe().Should().Contain("692");
    }

    [Fact]
    public void UnknownRejection_IsNotTreatedAsSuccess()
    {
        var status = Parse("""{"FingerPrintStatus":{"StatusList":[{"id":1,"cardReaderRecvStatus":3}]}}""");
        status!.Accepted.Should().BeFalse();
    }

    // The apply is asynchronous: an idle/queued response carries no StatusList and must not be read
    // as a verdict, or the caller reports a store that never happened.
    [Fact]
    public void NoStatusList_YieldsNoVerdict()
    {
        Parse("""{"FingerPrintStatus":{"totalStatus":1}}""").Should().BeNull();
        Parse("""{}""").Should().BeNull();
    }
}
