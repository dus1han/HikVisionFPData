using FluentAssertions;
using HikSync.Core.Logic;
using HikSync.Core.Models;

namespace HikSync.UnitTests;

public class SyncPlannerTests
{
    private static DeviceUser User(string emp, string? name = null) =>
        new() { EmployeeNo = emp, Name = name ?? $"User {emp}", UserType = "normal", Enabled = true };

    private static FingerprintTemplate Fp(string emp, int finger, params byte[] data) =>
        new() { EmployeeNo = emp, FingerIndex = finger, Template = data };

    [Fact]
    public void AddsMissingUsersAndFingerprints_SkipsUnchanged()
    {
        var inUsers = new[] { User("1001"), User("1002") };
        var inFps = new[] { Fp("1001", 1, 1, 2, 3) };
        var outUsers = new[] { User("1001") };            // 1001 identical, 1002 missing
        var outFps = Array.Empty<FingerprintTemplate>();  // fingerprint missing on OUT

        var plan = SyncPlanner.Build(inUsers, inFps, outUsers, outFps, deleteRemovedUsers: false);

        plan.UsersToUpsert.Select(u => u.EmployeeNo).Should().BeEquivalentTo(new[] { "1002" });
        plan.FingerprintsToUpsert.Should().ContainSingle(f => f.EmployeeNo == "1001" && f.FingerIndex == 1);
        plan.EmployeesToDelete.Should().BeEmpty();
    }

    [Fact]
    public void DetectsChangedUser()
    {
        var inUsers = new[] { User("1001", "New Name") };
        var outUsers = new[] { User("1001", "Old Name") };

        var plan = SyncPlanner.Build(inUsers, Array.Empty<FingerprintTemplate>(), outUsers, Array.Empty<FingerprintTemplate>(), false);

        plan.UsersToUpsert.Should().ContainSingle(u => u.EmployeeNo == "1001");
    }

    [Fact]
    public void DetectsChangedFingerprintTemplate()
    {
        var users = new[] { User("1001") };
        var inFps = new[] { Fp("1001", 1, 9, 9, 9) };
        var outFps = new[] { Fp("1001", 1, 1, 1, 1) };

        var plan = SyncPlanner.Build(users, inFps, users, outFps, false);

        plan.FingerprintsToUpsert.Should().ContainSingle(f => f.EmployeeNo == "1001" && f.FingerIndex == 1);
    }

    [Fact]
    public void BuildMissingOnly_AddsOnlyMissing_AndIgnoresChangedRecords()
    {
        var srcUsers = new[] { User("1001", "New Name"), User("1002") };
        var srcFps = new[] { Fp("1001", 1, 1, 2, 3), Fp("1002", 1, 4, 5, 6) };
        var tgtUsers = new[] { User("1001", "Old Name") };   // present but DIFFERENT
        var tgtFps = new[] { Fp("1001", 1, 9, 9, 9) };       // present but DIFFERENT

        var plan = SyncPlanner.BuildMissingOnly(srcUsers, srcFps, tgtUsers, tgtFps);

        // 1001 exists on the target, so it must NOT be re-sent (prevents two devices ping-ponging).
        plan.UsersToUpsert.Select(u => u.EmployeeNo).Should().BeEquivalentTo(new[] { "1002" });
        plan.FingerprintsToUpsert.Should().ContainSingle(f => f.EmployeeNo == "1002");
        plan.EmployeesToDelete.Should().BeEmpty();
    }

    [Fact]
    public void BuildMissingOnly_BothDirections_GivesEachDeviceTheOthersData()
    {
        var aUsers = new[] { User("1") };
        var aFps = new[] { Fp("1", 1, 1) };
        var bUsers = new[] { User("2") };
        var bFps = new[] { Fp("2", 1, 2) };

        var toB = SyncPlanner.BuildMissingOnly(aUsers, aFps, bUsers, bFps);
        var toA = SyncPlanner.BuildMissingOnly(bUsers, bFps, aUsers, aFps);

        toB.UsersToUpsert.Should().ContainSingle(u => u.EmployeeNo == "1");
        toB.FingerprintsToUpsert.Should().ContainSingle(f => f.EmployeeNo == "1");
        toA.UsersToUpsert.Should().ContainSingle(u => u.EmployeeNo == "2");
        toA.FingerprintsToUpsert.Should().ContainSingle(f => f.EmployeeNo == "2");
        // Applying both leaves each device holding {1, 2} — the union.
    }

    private static FingerprintTemplate Typed(string emp, int finger, string type, params byte[] data) =>
        new() { EmployeeNo = emp, FingerIndex = finger, FingerType = type, Template = data };

    // The terminal deduplicates biometrically: the same finger enrolled on two devices yields two
    // different templates under unrelated slot numbers, and the device declines the copy. Diffing by
    // slot re-pushed it on every cycle and the pair never converged.
    [Fact]
    public void BuildMissingOnly_PersonAlreadyEnrolledUnderAnotherSlot_IsNotPushedAgain()
    {
        var users = new[] { User("56") };
        var source = new[] { Fp("56", 2, 1, 1, 1) };   // enrolled in slot 2 here
        var target = new[] { Fp("56", 1, 7, 7, 7) };   // same person, slot 1, different bytes

        SyncPlanner.BuildMissingOnly(users, source, users, target)
            .FingerprintsToUpsert.Should().BeEmpty();
    }

    // A duress finger occupies the slot and the device refuses a second finger over it, so it has to
    // count as coverage — otherwise the sync retries that person forever.
    [Fact]
    public void BuildMissingOnly_NonAttendanceFingerOnTarget_CountsAsCoverage()
    {
        var users = new[] { User("692") };
        var source = new[] { Fp("692", 1, 1, 2, 3) };
        var target = new[] { Typed("692", 1, "dismissingFP", 9, 9) };

        SyncPlanner.BuildMissingOnly(users, source, users, target)
            .FingerprintsToUpsert.Should().BeEmpty();
    }

    // ...but a duress finger is device-local security config and must never be copied to the partner.
    [Fact]
    public void BuildMissingOnly_NeverCopiesNonAttendanceFingers()
    {
        var users = new[] { User("692") };
        var source = new[] { Typed("692", 1, "dismissingFP", 1, 2, 3) };

        SyncPlanner.BuildMissingOnly(users, source, users, Array.Empty<FingerprintTemplate>())
            .FingerprintsToUpsert.Should().BeEmpty();
    }

    [Fact]
    public void BuildMissingOnly_PersonWithNoFingerprintOnTarget_IsPushed()
    {
        var users = new[] { User("244") };
        var source = new[] { Fp("244", 1, 1, 2, 3) };

        SyncPlanner.BuildMissingOnly(users, source, users, Array.Empty<FingerprintTemplate>())
            .FingerprintsToUpsert.Should().ContainSingle(f => f.EmployeeNo == "244" && f.FingerIndex == 1);
    }

    [Fact]
    public void BuildMissingOnly_ExtraFingerOnSource_StillPropagates()
    {
        var users = new[] { User("77") };
        var source = new[] { Fp("77", 1, 1), Fp("77", 2, 2) };
        var target = new[] { Fp("77", 1, 1) };

        SyncPlanner.BuildMissingOnly(users, source, users, target)
            .FingerprintsToUpsert.Should().ContainSingle(f => f.FingerIndex == 2);
    }

    [Fact]
    public void DeletesRemovedUsers_OnlyWhenEnabled()
    {
        var inUsers = new[] { User("1001") };
        var outUsers = new[] { User("1001"), User("9999") };

        SyncPlanner.Build(inUsers, Array.Empty<FingerprintTemplate>(), outUsers, Array.Empty<FingerprintTemplate>(), false)
            .EmployeesToDelete.Should().BeEmpty();

        SyncPlanner.Build(inUsers, Array.Empty<FingerprintTemplate>(), outUsers, Array.Empty<FingerprintTemplate>(), true)
            .EmployeesToDelete.Should().BeEquivalentTo(new[] { "9999" });
    }
}
