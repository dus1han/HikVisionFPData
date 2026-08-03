using System.Diagnostics;
using System.Text.Json;
using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Service;

/// <summary>
/// Writes fingerprints by launching the service exe in one-shot <c>fp-sdk-apply</c> mode. The native
/// HCNetSDK runs entirely in that child process, so an access-violation crash kills only the child —
/// the service keeps capturing attendance and syncing users. A crash or timeout ⇒ every print in the
/// batch is reported failed and retried next cycle.
/// </summary>
public sealed class OutOfProcessSdkFingerprintWriter : ISdkFingerprintWriter
{
    private readonly SdkOptions _sdk;
    private readonly ILogger<OutOfProcessSdkFingerprintWriter> _logger;

    public OutOfProcessSdkFingerprintWriter(IOptions<SdkOptions> sdk, ILogger<OutOfProcessSdkFingerprintWriter> logger)
    {
        _sdk = sdk.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FingerprintWriteResult>> WriteAsync(
        DeviceEndpoint target, IReadOnlyList<FingerprintTemplate> prints, CancellationToken ct)
    {
        if (prints.Count == 0) return Array.Empty<FingerprintWriteResult>();

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Failed(prints, "cannot locate service executable to spawn SDK writer");

        var job = new SdkFpJob
        {
            Ip = target.Ip,
            Port = target.Port,
            User = target.Username,
            Pass = target.Password,
            NativeLibraryPath = _sdk.NativeLibraryPath,
            Prints = prints.Select(p => new SdkFpItem
            {
                EmployeeNo = p.EmployeeNo,
                FingerIndex = p.FingerIndex,
                FingerType = string.IsNullOrEmpty(p.FingerType) ? "normalFP" : p.FingerType,
                FingerDataBase64 = Convert.ToBase64String(p.Template),
            }).ToList(),
        };

        string jobPath = Path.Combine(Path.GetTempPath(), $"hiksync-fp-{Guid.NewGuid():N}.json");
        string resultPath = jobPath + ".result";
        try
        {
            await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job), ct);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                // The child resolves the native DLLs relative to the exe directory.
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add(SdkFingerprintApply.Verb);
            psi.ArgumentList.Add(jobPath);

            using var proc = Process.Start(psi);
            if (proc is null) return Failed(prints, "failed to start SDK writer process");

            // Bound the wait: SDK login + N writes. Generous, but capped so a hang can't stall the sync.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, prints.Count * 2 + 20)));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(proc);
                return Failed(prints, "SDK writer timed out");
            }

            if (!File.Exists(resultPath))
            {
                // No result file ⇒ the child crashed (native SDK access violation) before writing it.
                _logger.LogWarning("SDK fingerprint writer for {Ip} produced no result (exit {Code}) — likely a native crash; batch retried next cycle.",
                    target.Ip, proc.HasExited ? proc.ExitCode : -1);
                return Failed(prints, "SDK writer crashed (no result)");
            }

            var results = JsonSerializer.Deserialize<List<FingerprintWriteResult>>(await File.ReadAllTextAsync(resultPath, ct));
            return results ?? Failed(prints, "unreadable SDK writer result");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(prints, "SDK writer invocation failed: " + ex.Message);
        }
        finally
        {
            TryDelete(jobPath);
            TryDelete(resultPath);
        }
    }

    private static IReadOnlyList<FingerprintWriteResult> Failed(IReadOnlyList<FingerprintTemplate> prints, string error) =>
        prints.Select(p => new FingerprintWriteResult { EmployeeNo = p.EmployeeNo, FingerIndex = p.FingerIndex, Ok = false, Error = error }).ToList();

    private static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
