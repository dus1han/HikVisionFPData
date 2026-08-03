using System.Text.Json;
using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Models;
using HikSync.Device.Hikvision;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Service;

/// <summary>Job written by the service, read by the one-shot child that does the SDK writes.</summary>
public sealed class SdkFpJob
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 8000;
    public string User { get; set; } = "admin";
    public string Pass { get; set; } = string.Empty;
    public string NativeLibraryPath { get; set; } = "native";
    public List<SdkFpItem> Prints { get; set; } = new();
}

public sealed class SdkFpItem
{
    public string EmployeeNo { get; set; } = string.Empty;
    public int FingerIndex { get; set; }
    public string FingerType { get; set; } = "normalFP";
    public string FingerDataBase64 { get; set; } = string.Empty;
}

/// <summary>
/// One-shot mode: <c>HikSync.Service.exe fp-sdk-apply &lt;jobFile&gt;</c>. Reads the job, logs in over the
/// HCNetSDK, writes each fingerprint, and writes <c>&lt;jobFile&gt;.result</c>. Runs in a throwaway child
/// process so a native SDK crash cannot take down the service. Always exits 0 (results carry per-item
/// success) unless it cannot read the job or the SDK login fails.
/// </summary>
public static class SdkFingerprintApply
{
    public const string Verb = "fp-sdk-apply";

    public static async Task<int> RunAsync(string jobPath)
    {
        SdkFpJob job;
        try { job = JsonSerializer.Deserialize<SdkFpJob>(await File.ReadAllTextAsync(jobPath))!; }
        catch (Exception ex) { Console.Error.WriteLine("fp-sdk-apply: cannot read job: " + ex.Message); return 2; }

        var results = new List<FingerprintWriteResult>(job.Prints.Count);
        using var lf = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));

        var sdkOptions = Options.Create(new SdkOptions
        {
            Transport = "sdk",
            NativeLibraryPath = job.NativeLibraryPath,
            LoginMode = 2,
        });

        var manager = new HcNetSdkManager(sdkOptions, lf.CreateLogger<HcNetSdkManager>());
        var factory = new HikvisionDeviceFactory(manager, lf);
        var endpoint = new DeviceEndpoint { Ip = job.Ip, Port = job.Port, Username = job.User, Password = job.Pass };

        try
        {
            await using var device = await factory.ConnectAsync(endpoint, CancellationToken.None);
            foreach (var p in job.Prints)
            {
                var r = new FingerprintWriteResult { EmployeeNo = p.EmployeeNo, FingerIndex = p.FingerIndex };
                try
                {
                    var fp = new FingerprintTemplate
                    {
                        EmployeeNo = p.EmployeeNo,
                        FingerIndex = p.FingerIndex,
                        FingerType = p.FingerType,
                        Template = Convert.FromBase64String(p.FingerDataBase64),
                    };
                    await device.UpsertFingerprintAsync(fp, CancellationToken.None);
                    r.Ok = true;
                }
                catch (Exception ex) { r.Ok = false; r.Error = ex.Message; }
                results.Add(r);
            }
        }
        catch (Exception ex)
        {
            // Login/connect failed — mark everything failed with the reason so the service can report it.
            foreach (var p in job.Prints)
                results.Add(new FingerprintWriteResult { EmployeeNo = p.EmployeeNo, FingerIndex = p.FingerIndex, Ok = false, Error = "SDK connect: " + ex.Message });
        }

        await File.WriteAllTextAsync(jobPath + ".result", JsonSerializer.Serialize(results));
        return 0;
    }
}
