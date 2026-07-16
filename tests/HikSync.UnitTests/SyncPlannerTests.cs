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
