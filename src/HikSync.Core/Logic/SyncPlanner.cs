using HikSync.Core.Models;

namespace HikSync.Core.Logic;

/// <summary>The set of write operations needed to make OUT match IN.</summary>
public sealed class SyncPlan
{
    public List<DeviceUser> UsersToUpsert { get; } = new();
    public List<FingerprintTemplate> FingerprintsToUpsert { get; } = new();
    public List<string> EmployeesToDelete { get; } = new();

    public bool IsEmpty =>
        UsersToUpsert.Count == 0 && FingerprintsToUpsert.Count == 0 && EmployeesToDelete.Count == 0;
}

/// <summary>
/// Pure diff engine: given IN and OUT snapshots, compute the minimal set of upserts/deletes.
/// IN is the master. No device I/O here so it is fully unit-testable.
/// </summary>
public static class SyncPlanner
{
    /// <summary>
    /// Additive plan: only what exists on the source but is MISSING on the target.
    /// Used for the bidirectional (union) sync so a couple ends up holding the same full set.
    /// Deliberately does NOT flag changed records — otherwise two devices would overwrite each
    /// other's differing copies on every cycle.
    ///
    /// Fingerprint types are treated asymmetrically on purpose:
    ///  * only attendance fingers are COPIED (a duress finger is device-local security config), while
    ///  * every enrolled finger COUNTS as coverage on the target.
    /// Counting only attendance fingers would make the sync re-push a slot that is already occupied
    /// by a duress finger every cycle — the device refuses it as an already-enrolled finger, and the
    /// pair never converges.
    ///
    /// Coverage is compared per PERSON, not per finger slot. The terminal deduplicates biometrically:
    /// it refuses a finger it already holds even when the template bytes differ, because the same
    /// finger enrolled twice produces two different blobs. Two devices enrolled independently
    /// therefore hold the same person's finger under unrelated slot numbers, and a slot-by-slot diff
    /// would push a copy the device silently declines — forever. A person is considered covered once
    /// the target holds at least as many fingers for them as the source has to offer.
    /// </summary>
    public static SyncPlan BuildMissingOnly(
        IReadOnlyCollection<DeviceUser> sourceUsers,
        IReadOnlyCollection<FingerprintTemplate> sourceFingerprints,
        IReadOnlyCollection<DeviceUser> targetUsers,
        IReadOnlyCollection<FingerprintTemplate> targetFingerprints)
    {
        var plan = new SyncPlan();

        var targetEmployees = new HashSet<string>(targetUsers.Select(u => u.EmployeeNo), StringComparer.Ordinal);
        foreach (var user in sourceUsers)
            if (!targetEmployees.Contains(user.EmployeeNo))
                plan.UsersToUpsert.Add(user);

        var targetCountByEmployee = targetFingerprints
            .GroupBy(f => f.EmployeeNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var targetSlots = new HashSet<(string, int)>(targetFingerprints.Select(f => f.Key));

        foreach (var group in sourceFingerprints.Where(f => f.IsAttendanceFinger)
                                                .GroupBy(f => f.EmployeeNo, StringComparer.Ordinal))
        {
            if (targetCountByEmployee.GetValueOrDefault(group.Key) >= group.Count()) continue;
            foreach (var fp in group)
                if (!targetSlots.Contains(fp.Key))
                    plan.FingerprintsToUpsert.Add(fp);
        }

        return plan;
    }

    public static SyncPlan Build(
        IReadOnlyCollection<DeviceUser> inUsers,
        IReadOnlyCollection<FingerprintTemplate> inFingerprints,
        IReadOnlyCollection<DeviceUser> outUsers,
        IReadOnlyCollection<FingerprintTemplate> outFingerprints,
        bool deleteRemovedUsers)
    {
        var plan = new SyncPlan();

        var outUsersByEmp = outUsers.ToDictionary(u => u.EmployeeNo, StringComparer.Ordinal);
        var outFpByKey = outFingerprints.ToDictionary(f => f.Key);

        // Users: add or update where missing/changed on OUT.
        foreach (var inUser in inUsers)
        {
            if (!outUsersByEmp.TryGetValue(inUser.EmployeeNo, out var outUser) ||
                !string.Equals(inUser.SyncSignature(), outUser.SyncSignature(), StringComparison.Ordinal))
            {
                plan.UsersToUpsert.Add(inUser);
            }
        }

        // Fingerprints: add or update where missing/changed on OUT (create user first — see collector).
        foreach (var inFp in inFingerprints)
        {
            if (!outFpByKey.TryGetValue(inFp.Key, out var outFp) ||
                !inFp.Template.AsSpan().SequenceEqual(outFp.Template))
            {
                plan.FingerprintsToUpsert.Add(inFp);
            }
        }

        // Deletes: OUT users absent from IN (optional).
        if (deleteRemovedUsers)
        {
            var inEmps = new HashSet<string>(inUsers.Select(u => u.EmployeeNo), StringComparer.Ordinal);
            foreach (var outUser in outUsers)
            {
                if (!inEmps.Contains(outUser.EmployeeNo))
                    plan.EmployeesToDelete.Add(outUser.EmployeeNo);
            }
        }

        return plan;
    }
}
