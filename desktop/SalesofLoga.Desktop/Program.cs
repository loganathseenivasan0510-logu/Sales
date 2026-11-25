using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SalesofLoga.Desktop.Components;
using SalesofLoga.Desktop.Data;
using SalesofLoga.Desktop.Services;

var options = CommandLine.Parse(args);
var paths = new AppPaths(options.DataDir);
var fileLogs = new FileLoggerProvider(paths.LogDir);
using var bootLoggerFactory = LoggerFactory.Create(b => b.AddProvider(fileLogs));
var bootLog = bootLoggerFactory.CreateLogger("Startup");

// ---------- Nur eine Instanz pro Benutzer und Datenordner ----------
var mutexName = "Local\\SalesofLoga-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDir.ToLowerInvariant())))[..16];
using var instanceMutex = new Mutex(true, mutexName, out var firstInstance);
if (!firstInstance && !options.SelfTest && !options.SecurityCheck)
{
    bootLog.LogInformation("Second start detected – opening the running instance");
    try
    {
        var info = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(paths.InstanceFile));
        if (info != null && !options.NoBrowser) Browser.Open($"http://127.0.0.1:{info["port"]}/auth?k={info["token"]}", bootLog);
    }
    catch (Exception ex) { bootLog.LogError(ex, "Running instance could not be opened"); }
    fileLogs.Dispose();
    return 0;
}

try
{
    var user = UserIdentity.Current(bootLog);
    var repo = new SalesRepository(options.SelfTest ? Path.Combine(paths.DataDir, $"selftest-{Guid.NewGuid():N}.db") : paths.DbPath,
        bootLoggerFactory.CreateLogger("Data.SalesRepository"));

    if (options.SelfTest) return SelfTest.Run(repo, user, bootLog);

    repo.Initialize();
    try { repo.DailyBackup(paths.BackupDir); } catch (Exception ex) { bootLog.LogError(ex, "Daily backup failed"); }

    if (options.ImportDir != null)
    {
        var r = await repo.ImportWebJsonAsync(options.ImportDir, user);
        bootLog.LogInformation("Command-line import: {Records} records, {Skipped} skipped, {Rejected} rejected attachments", r.Records, r.Skipped, r.RejectedAttachments);
    }

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    });
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(fileLogs);
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

    builder.WebHost.UseStaticWebAssets();
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Listen(IPAddress.Loopback, options.Port); // nur 127.0.0.1 – im Netzwerk nicht erreichbar
        k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    });

    var runtime = new AppRuntime(paths, user, repo, fileLogs, options);
    builder.Services.AddSingleton(runtime);
    builder.Services.AddSingleton(repo);
    builder.Services.AddSingleton<SecurityCheckService>();
    builder.Services.AddSingleton<ShutdownWatcher>();
    builder.Services.AddSingleton<CircuitHandler>(sp => sp.GetRequiredService<ShutdownWatcher>());
    builder.Services.AddScoped<ToastService>();
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents(o => o.DetailedErrors = false)
        .AddHubOptions(o => o.MaximumReceiveMessageSize = 256 * 1024);

    var app = builder.Build();
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
    try
    {
        var logoFile = app.Environment.WebRootFileProvider.GetFileInfo("assets/centrotherm-logo-white.png");
        if (logoFile.Exists)
        {
            using var s = logoFile.CreateReadStream(); using var ms = new MemoryStream(); s.CopyTo(ms);
            runtime.LogoDataUri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
    }
    catch (Exception ex) { log.LogWarning(ex, "Logo for reports not found"); }

    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseMiddleware<LocalAccessMiddleware>();
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseAntiforgery();
    app.MapStaticAssets();
    Endpoints.Map(app);
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    await app.StartAsync();
    runtime.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    if (firstInstance) WriteInstanceFile(paths.InstanceFile, runtime, log);
    log.LogInformation("SalesofLoga {Version} started for {User} ({Account}) on {Url}, data: {Db}, SQLite {Sqlite}",
        runtime.Version, user.Name, user.Account, runtime.BaseUrl, paths.DbPath, SalesofLoga.Desktop.Infrastructure.SqliteConnection.LibraryVersion);

    if (options.SecurityCheck)
    {
        var run = await app.Services.GetRequiredService<SecurityCheckService>().RunAsync(user);
        var report = options.ReportPath ?? Path.Combine(paths.DataDir, "security-report.html");
        await File.WriteAllTextAsync(report, SecurityReport.ToHtml(run, runtime), Encoding.UTF8);
        log.LogInformation("Security check finished: {Pass} passed, {Warn} warnings, {Fail} failed → {Report}", run.Passed, run.Warnings, run.Failed, report);
        Console.WriteLine($"Security check: {run.Passed} passed, {run.Warnings} warnings, {run.Failed} failed → {report}");
        await app.StopAsync();
        return run.Failed == 0 ? 0 : 2;
    }

    if (!options.NoBrowser) Browser.Open(runtime.LoginUrl, log);
    app.Services.GetRequiredService<ShutdownWatcher>().Arm();
    await app.WaitForShutdownAsync();
    log.LogInformation("SalesofLoga stopped");
    return 0;
}
catch (Exception ex)
{
    bootLog.LogCritical(ex, "Fatal error during start");
    Console.Error.WriteLine(ex);
    if (OperatingSystem.IsWindows() && !options.NoBrowser && !options.SecurityCheck) NativeDialogs.Error($"SalesofLoga could not start:\n\n{ex.Message}\n\nDetails: {paths.LogDir}");
    return 1;
}
finally
{
    try { if (firstInstance && File.Exists(paths.InstanceFile)) File.Delete(paths.InstanceFile); } catch { /* ignore */ }
    fileLogs.Dispose();
}

static void WriteInstanceFile(string path, AppRuntime rt, ILogger log)
{
    File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string> { ["port"] = rt.Port.ToString(), ["token"] = rt.Token, ["pid"] = Environment.ProcessId.ToString() }));
    try
    {
        if (OperatingSystem.IsWindows())
        {
            // Nur der eigene Benutzer darf die Datei lesen (enthält das Sitzungs-Token)
            var fi = new FileInfo(path);
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
    catch (Exception ex) { log.LogWarning(ex, "Permissions of {File} could not be restricted", path); }
}

static class Browser
{
    public static void Open(string url, ILogger log)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Edge im App-Modus wirkt wie ein eigenes Programmfenster; sonst Standardbrowser
                try { Process.Start(new ProcessStartInfo("msedge", $"--app={url}") { UseShellExecute = true }); return; }
                catch (Exception ex) { log.LogInformation("Edge app mode not available ({Message}) – using default browser", ex.Message); }
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch (Exception ex) { log.LogWarning(ex, "Browser could not be started. Open manually: {Url}", url.Split('?')[0]); }
    }
}

/// <summary>Beendet die App automatisch, wenn das letzte Browserfenster 90 Sekunden geschlossen ist.</summary>
public sealed class ShutdownWatcher : CircuitHandler
{
    private readonly IHostApplicationLifetime _life;
    private readonly AppRuntime _rt;
    private readonly ILogger<ShutdownWatcher> _log;
    private int _open;
    private bool _armed;
    private CancellationTokenSource? _pending;

    public ShutdownWatcher(IHostApplicationLifetime life, AppRuntime rt, ILogger<ShutdownWatcher> log) { _life = life; _rt = rt; _log = log; }

    public void Arm() { _armed = !_rt.Options.Stay; }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        Interlocked.Increment(ref _open);
        _pending?.Cancel();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        if (Interlocked.Decrement(ref _open) > 0 || !_armed) return Task.CompletedTask;
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(90), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || Volatile.Read(ref _open) > 0) return;
            _log.LogInformation("No open window for 90 s – stopping");
            _life.StopApplication();
        }, TaskScheduler.Default);
        return Task.CompletedTask;
    }
}

static class NativeDialogs
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    public static void Error(string text) { try { MessageBoxW(IntPtr.Zero, text, "SalesofLoga", 0x10); } catch { /* ignore */ } }
}
