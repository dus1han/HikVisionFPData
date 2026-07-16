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
