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

        var targetPrints = new HashSet<(string, int)>(targetFingerprints.Select(f => f.Key));
        foreach (var fp in sourceFingerprints)
            if (!targetPrints.Contains(fp.Key))
                plan.FingerprintsToUpsert.Add(fp);

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
