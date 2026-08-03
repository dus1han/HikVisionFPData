using HikSync.Application;
using HikSync.Core.Configuration;
using HikSync.Data;
using HikSync.Device;
using HikSync.Push;
using HikSync.Service.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

// A Windows service starts with its working directory set to C:\Windows\System32, so Serilog's
// relative "logs/" path would write there instead of next to the exe. AddWindowsService fixes the
// content root (config resolution) but not the working directory, so pin it here — before the
// builder reads configuration.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// One-shot SDK fingerprint writer: `HikSync.Service.exe fp-sdk-apply <jobFile>`. The service spawns
// itself in this mode so the native HCNetSDK runs in a throwaway child — a crash there cannot take
// down the service. Handled before the host is built; it never starts the workers.
if (args.Length == 2 && args[0] == HikSync.Service.SdkFingerprintApply.Verb)
    return await HikSync.Service.SdkFingerprintApply.RunAsync(args[1]);

var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service (also runs fine as a console app for local debugging).
builder.Services.AddWindowsService(options => options.ServiceName = "HikSync");

// Serilog from configuration.
builder.Services.AddSerilog((services, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Strongly-typed, validated options.
builder.Services.AddOptions<LocalDatabaseOptions>().Bind(builder.Configuration.GetSection(LocalDatabaseOptions.Section))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SdkOptions>().Bind(builder.Configuration.GetSection(SdkOptions.Section)).ValidateOnStart();
builder.Services.AddOptions<AttendanceOptions>().Bind(builder.Configuration.GetSection(AttendanceOptions.Section)).ValidateOnStart();
builder.Services.AddOptions<SyncOptions>().Bind(builder.Configuration.GetSection(SyncOptions.Section)).ValidateOnStart();
builder.Services.AddOptions<PushOptions>().Bind(builder.Configuration.GetSection(PushOptions.Section)).ValidateOnStart();
builder.Services.AddOptions<LogOptions>().Bind(builder.Configuration.GetSection(LogOptions.Section)).ValidateOnStart();

// Composition.
builder.Services.AddHikSyncData();
builder.Services.AddHikSyncDevices();
builder.Services.AddHikSyncPush();

// Out-of-process SDK fingerprint writer (spawns this exe in fp-sdk-apply mode).
builder.Services.AddSingleton<HikSync.Core.Abstractions.ISdkFingerprintWriter, HikSync.Service.OutOfProcessSdkFingerprintWriter>();

builder.Services.AddSingleton<HealthState>();
builder.Services.AddSingleton<OperationLogger>();
builder.Services.AddSingleton<AttendanceCollector>();
builder.Services.AddSingleton<DeviceSyncService>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddSingleton<LogRetentionService>();

builder.Services.AddHostedService<AttendanceWorker>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHostedService<PushWorker>();
builder.Services.AddHostedService<LogRetentionWorker>();

var host = builder.Build();

// Apply local DB migrations before the workers start.
using (var scope = host.Services.CreateScope())
{
    var dbOptions = scope.ServiceProvider.GetRequiredService<IOptions<LocalDatabaseOptions>>().Value;
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    if (dbOptions.MigrateOnStartup)
        DatabaseMigrator.Migrate(dbOptions.ConnectionString, logger);
}

host.Run();
return 0;
