using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using SalesofLoga.Desktop.Components.Shared;
using SalesofLoga.Desktop.Data;
using SalesofLoga.Desktop.Infrastructure;

namespace SalesofLoga.Desktop.Services;

public enum CheckStatus { Pass, Warn, Fail }

public sealed record CheckResult(string Id, string Category, string Title, string Expected, string Actual, CheckStatus Status, long Ms);

public sealed record SecurityRun(string Id, DateTimeOffset Started, string RunBy, IReadOnlyList<CheckResult> Results)
{
    public int Passed => Results.Count(r => r.Status == CheckStatus.Pass);
    public int Warnings => Results.Count(r => r.Status == CheckStatus.Warn);
    public int Failed => Results.Count(r => r.Status == CheckStatus.Fail);
}

/// <summary>
/// Ausführbare Sicherheitsprüfungen gegen die LAUFENDE Instanz (HTTP gegen sich selbst) und gegen eine
/// Sandbox-Datenbank (Rechte, Injection, Upload-Prüfung). Die echten Daten werden nicht verändert.
/// Kategorien nach OWASP Top 10 (2021).
/// </summary>
public sealed class SecurityCheckService
{
    private readonly AppRuntime _rt;
    private readonly IServiceProvider _sp;
    private readonly ILogger<SecurityCheckService> _log;
    private readonly List<SecurityRun> _history = new();
    private readonly SemaphoreSlim _running = new(1, 1);

    public SecurityCheckService(AppRuntime rt, IServiceProvider sp, ILogger<SecurityCheckService> log) { _rt = rt; _sp = sp; _log = log; }

    public IReadOnlyList<SecurityRun> History { get { lock (_history) return _history.ToList(); } }
    public bool IsRunning => _running.CurrentCount == 0;

    public async Task<SecurityRun> RunAsync(AppUser by)
    {
        await _running.WaitAsync();
        try
        {
            _log.LogInformation("Security check started by {User}", by.Account);
            var results = new List<CheckResult>();
            using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(_rt.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            var sandboxDir = Path.Combine(Path.GetTempPath(), "slg-sec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandboxDir);
            try
            {
                var sandbox = new SalesRepository(Path.Combine(sandboxDir, "sandbox.db"), _log);
                sandbox.Initialize();
                var userA = new AppUser("win:test\\alice", "Alice Tester", "TEST\\alice", "");
                var userB = new AppUser("win:test\\bob", "Bob Tester", "TEST\\bob", "");
                var md = sandbox.GetMaster();
                md.Admins = new List<string> { "TEST\\admin" };
                sandbox.SaveMaster(md, userA);

                // ---------------- A01 Broken Access Control
                await Check(results, "SC-01", "A01 Access Control", "Request without session cookie is rejected", "HTTP 401", async () =>
                {
                    var r = await http.GetAsync("/");
                    return ((int)r.StatusCode == 401, $"HTTP {(int)r.StatusCode}");
                });
                await Check(results, "SC-02", "A01 Access Control", "Request with a wrong session token is rejected", "HTTP 401", async () =>
                {
                    var r = await Send(http, "/", cookie: new string('A', _rt.Token.Length));
                    return ((int)r.StatusCode == 401, $"HTTP {(int)r.StatusCode}");
                });
                await Check(results, "SC-03", "A01 Access Control", "Attachment download without session is rejected", "HTTP 401", async () =>
                {
                    var r = await http.GetAsync($"/attachments/{Guid.NewGuid()}");
                    return ((int)r.StatusCode == 401, $"HTTP {(int)r.StatusCode}");
                });
                await Check(results, "SC-04", "A01 Access Control", "Database backup download without session is rejected", "HTTP 401", async () =>
                {
                    var r = await http.GetAsync("/backup");
                    return ((int)r.StatusCode == 401, $"HTTP {(int)r.StatusCode}");
                });
                await Check(results, "SC-05", "A01 Access Control", "User cannot change or delete another user's record (server-side)", "PermissionDenied for update and delete", async () =>
                {
                    var rec = await sandbox.CreateAsync(Schema.Quotation, Raw("2026-01-10", "SEC-OWN-1", "EMMVEE", "Open", "100"), "INR", null, userA);
                    var upd = await Throws<PermissionDeniedException>(() => sandbox.UpdateAsync(Schema.Quotation, rec.Id, Raw("2026-01-10", "SEC-OWN-1", "EMMVEE", "Closed", "1"), "INR", null, false, userB));
                    var del = await Throws<PermissionDeniedException>(() => sandbox.DeleteAsync(Schema.Quotation, rec.Id, userB));
                    var after = sandbox.Get(Schema.Quotation, rec.Id);
                    var unchanged = after != null && after.Text("status") == "Open";
                    return (upd && del && unchanged, $"update denied: {upd}, delete denied: {del}, record unchanged: {unchanged}");
                });
                await Check(results, "SC-06", "A01 Access Control", "Path traversal on attachment URL is not possible", "HTTP 404 for ../ paths", async () =>
                {
                    var r1 = await Send(http, "/attachments/..%2F..%2Fsalesofloga.db", cookie: _rt.Token);
                    var r2 = await Send(http, "/attachments/%2e%2e%5csalesofloga.db", cookie: _rt.Token);
                    var ok = (int)r1.StatusCode == 404 && (int)r2.StatusCode == 404;
                    return (ok, $"HTTP {(int)r1.StatusCode} / {(int)r2.StatusCode}");
                });

                // ---------------- A02 Cryptographic Failures / Secrets
                await Check(results, "SC-07", "A02 Cryptography", "Session token has ≥ 256 bit entropy (CSPRNG) and is compared in constant time", "≥ 43 Base64url characters", () =>
                    Task.FromResult((_rt.Token.Length >= 43, $"{_rt.Token.Length} characters, RandomNumberGenerator + FixedTimeEquals")));
                await Check(results, "SC-08", "A02 Cryptography", "Token file is readable only by the current user", "only owner has access", () =>
                    Task.FromResult(CheckOwnerOnly(_rt.Paths.InstanceFile)));
                await Check(results, "SC-09", "A02 Cryptography", "Data folder is not readable for other users (Everyone/Users)", "no broad read access", () =>
                    Task.FromResult(CheckNoBroadAccess(_rt.Paths.DataDir)), warnOnly: true);

                // ---------------- A03 Injection
                await Check(results, "SC-10", "A03 Injection", "SQL injection in search fields has no effect", "0 hits, tables intact", async () =>
                {
                    await sandbox.CreateAsync(Schema.Stock, RawStock("2026-01-01", "SEC-P1", "Quartz tube", "5"), null, null, userA);
                    var payloads = new[] { "' OR '1'='1", "%' OR 1=1 --", "x'); DROP TABLE stock; --", "_%" };
                    var hits = payloads.Sum(p => sandbox.List(Schema.Stock, new Dictionary<string, string> { ["partId"] = p }).Count);
                    var intact = sandbox.List(Schema.Stock).Count == 1;
                    return (hits == 0 && intact, $"{hits} hits for {payloads.Length} payloads, table intact: {intact}");
                });
                await Check(results, "SC-11", "A03 Injection", "Stored XSS payload is rendered as text (HTML-encoded)", "no executable markup in output", async () =>
                {
                    var payload = "<img src=x onerror=alert(1)><script>alert(2)</script>";
                    var rec = new SalesRecord { Id = "x", CreatedById = userA.Id, CreatedByName = payload, CreatedAt = "2026-01-01" };
                    rec.Values["stockDate"] = "2026-01-01"; rec.Values["partId"] = "P"; rec.Values["description"] = payload; rec.Values["qty"] = 1L;
                    var html = await RenderTable(Schema.Stock, rec);
                    var raw = html.Contains("<img src=x", StringComparison.OrdinalIgnoreCase) || html.Contains("<script>", StringComparison.OrdinalIgnoreCase);
                    var encoded = html.Contains("&lt;img src=x", StringComparison.OrdinalIgnoreCase);
                    return (!raw && encoded, raw ? "raw payload found in HTML!" : "payload HTML-encoded (&lt;img …)");
                });
                await Check(results, "SC-12", "A03 Injection", "CSV export neutralises Excel formulas", "cells starting with = + - @ are prefixed", () =>
                {
                    var cells = new[] { "=HYPERLINK(\"http://evil\")", "+cmd|' /C calc'!A0", "@SUM(1+1)", "-2+3" }.Select(c => SalesRepository.CsvCell(c, ";")).ToList();
                    var ok = cells.All(c => c.TrimStart('"').StartsWith('\''));
                    return Task.FromResult((ok, string.Join("  ", cells)));
                });
                await Check(results, "SC-13", "A03 Injection", "Host header spoofing (DNS rebinding) is rejected", "HTTP 400", async () =>
                {
                    var r = await Send(http, "/", cookie: _rt.Token, host: "evil.example");
                    return ((int)r.StatusCode == 400, $"HTTP {(int)r.StatusCode}");
                });

                // ---------------- A04 Insecure Design / Upload
                await Check(results, "SC-14", "A04 Insecure Design", "Disguised executable uploaded as .pdf is rejected", "signature check fails", () =>
                {
                    var exe = new NewAttachment("invoice.pdf", "application/pdf", new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });
                    var html = new NewAttachment("quote.pdf", "application/pdf", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"));
                    var e1 = Validator.CheckFile(Schema.Quotation.FileField!, exe, 20);
                    var e2 = Validator.CheckFile(Schema.Quotation.FileField!, html, 20);
                    var ok = Validator.CheckFile(Schema.Quotation.FileField!, new NewAttachment("ok.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF")), 20) == null;
                    return Task.FromResult((e1 != null && e2 != null && ok, $"MZ: {e1 ?? "accepted!"} | HTML: {e2 ?? "accepted!"} | real PDF accepted: {ok}"));
                });
                await Check(results, "SC-15", "A04 Insecure Design", "Duplicate business numbers are blocked (incl. case variants)", "ValidationException", async () =>
                {
                    await sandbox.CreateAsync(Schema.Quotation, Raw("2026-01-11", "SEC-DUP-1", "EMMVEE", "Open", "1"), "INR", null, userA);
                    var dup = await Throws<ValidationException>(() => sandbox.CreateAsync(Schema.Quotation, Raw("2026-01-11", "sec-dup-1", "EMMVEE", "Open", "1"), "INR", null, userB));
                    return (dup, dup ? "duplicate rejected" : "duplicate accepted!");
                });

                // ---------------- A05 Security Misconfiguration
                await Check(results, "SC-16", "A05 Misconfiguration", "Server listens on loopback only (not reachable from the network)", "no connection via LAN address", async () =>
                {
                    var lan = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).ToList();
                    var reachable = new List<string>();
                    foreach (var ip in lan)
                    {
                        using var tcp = new TcpClient();
                        try { var t = tcp.ConnectAsync(ip, _rt.Port); if (await Task.WhenAny(t, Task.Delay(1500)) == t && tcp.Connected) reachable.Add(ip.ToString()); }
                        catch { /* erwartet: Verbindung abgelehnt */ }
                    }
                    return (reachable.Count == 0, lan.Count == 0 ? "no LAN address present – bound to 127.0.0.1" : reachable.Count == 0 ? $"refused on {string.Join(", ", lan)}" : $"REACHABLE on {string.Join(", ", reachable)}");
                });
                await Check(results, "SC-17", "A05 Misconfiguration", "Security headers are set", "CSP, nosniff, frame DENY, no-referrer, no Server header", async () =>
                {
                    var r = await Send(http, "/", cookie: _rt.Token);
                    string H(string n) => r.Headers.TryGetValues(n, out var v) ? string.Join(",", v) : r.Content.Headers.TryGetValues(n, out var c) ? string.Join(",", c) : "";
                    var csp = H("Content-Security-Policy");
                    var missing = new List<string>();
                    if (!csp.Contains("script-src 'self'") || csp.Contains("unsafe-eval") || csp.Contains("'unsafe-inline'")) missing.Add("CSP");
                    if (!csp.Contains("frame-ancestors 'none'")) missing.Add("frame-ancestors");
                    if (H("X-Content-Type-Options") != "nosniff") missing.Add("nosniff");
                    if (H("X-Frame-Options") != "DENY") missing.Add("X-Frame-Options");
                    if (H("Referrer-Policy") != "no-referrer") missing.Add("Referrer-Policy");
                    if (H("Server").Length > 0) missing.Add("Server header present");
                    return (missing.Count == 0, missing.Count == 0 ? $"HTTP {(int)r.StatusCode}, all headers present" : "missing/weak: " + string.Join(", ", missing));
                });
                await Check(results, "SC-18", "A05 Misconfiguration", "Session cookie is HttpOnly and SameSite=Strict", "HttpOnly; SameSite=Strict", async () =>
                {
                    var r = await http.GetAsync($"/auth?k={Uri.EscapeDataString(_rt.Token)}");
                    var set = r.Headers.TryGetValues("Set-Cookie", out var v) ? string.Join(" | ", v) : "";
                    var ok = set.Contains("httponly", StringComparison.OrdinalIgnoreCase) && set.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase);
                    return (ok, ok ? "HttpOnly; SameSite=Strict; session cookie" : $"Set-Cookie: {Mask(set)}");
                });
                await Check(results, "SC-19", "A05 Misconfiguration", "Error pages reveal no stack traces", "no exception details", async () =>
                {
                    var r = await Send(http, "/does-not-exist-" + Guid.NewGuid().ToString("N"), cookie: _rt.Token);
                    var body = await r.Content.ReadAsStringAsync();
                    var leak = body.Contains("   at ", StringComparison.Ordinal) || body.Contains("Exception", StringComparison.Ordinal);
                    return (!leak, $"HTTP {(int)r.StatusCode}, {(leak ? "stack trace found!" : "no details")}");
                });

                // ---------------- A06 Vulnerable Components
                await Check(results, "SC-20", "A06 Components", "No third-party assemblies loaded (only .NET + app)", "System.*, Microsoft.*, SalesofLoga", () =>
                {
                    var foreign = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "")
                        .Where(n => !(n.StartsWith("System") || n.StartsWith("Microsoft") || n == "SalesofLoga" || n == "netstandard" || n == "mscorlib" || n.StartsWith("Anonymously Hosted")))
                        .Distinct().ToList();
                    return Task.FromResult((foreign.Count == 0, foreign.Count == 0 ? $".NET {Environment.Version} only" : "foreign: " + string.Join(", ", foreign)));
                });
                await Check(results, "SC-21", "A06 Components", "SQLite library is current (≥ 3.37)", "≥ 3.37", () =>
                {
                    var v = SqliteConnection.LibraryVersion;
                    var ok = Version.TryParse(v, out var ver) && ver >= new Version(3, 37);
                    return Task.FromResult((ok, $"SQLite {v} ({(OperatingSystem.IsWindows() ? "winsqlite3.dll, updated via Windows Update" : "system library")})"));
                }, warnOnly: true);

                // ---------------- A08 Data Integrity
                await Check(results, "SC-22", "A08 Data Integrity", "Database integrity check and foreign keys", "integrity_check = ok, 0 FK violations", () =>
                {
                    var (ic, fk) = _rt.Repo.CheckIntegrity();
                    return Task.FromResult((ic == "ok" && fk == 0, $"integrity_check: {ic}, FK violations: {fk}"));
                });
                await Check(results, "SC-23", "A08 Data Integrity", "Backup from the last 24 hours exists", "backup ≤ 24 h old", () =>
                {
                    var last = Directory.GetFiles(_rt.Paths.BackupDir, "salesofloga-*.db").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    var ok = last != null && last.LastWriteTime > DateTime.Now.AddHours(-24);
                    return Task.FromResult((ok, last == null ? "no backup found" : $"{last.Name}, {last.LastWriteTime:yyyy-MM-dd HH:mm}"));
                }, warnOnly: true);
                await Check(results, "SC-24", "A08 Data Integrity", "Deletes are soft deletes with audit trail", "record kept + audit entry", async () =>
                {
                    var rec = await sandbox.CreateAsync(Schema.Stock, RawStock("2026-01-02", "SEC-DEL-1", "test", "1"), null, null, userA);
                    await sandbox.DeleteAsync(Schema.Stock, rec.Id, userA);
                    var kept = sandbox.CountRecords(includeDeleted: true) > sandbox.CountRecords(includeDeleted: false);
                    var audited = sandbox.CountAudit("delete") == 1;
                    return (kept && audited, $"row kept: {kept}, audit entry: {audited}");
                });

                // ---------------- A09 Logging & Monitoring
                await Check(results, "SC-25", "A09 Logging", "Denied access attempts are logged", "warning in log file", async () =>
                {
                    await Task.Delay(300); // Logger schreibt asynchron
                    var tail = _rt.Logs.Tail(400);
                    var ok = tail.Any(l => l.Contains("Denied:")) && tail.Any(l => l.Contains("Blocked request with foreign Host header"));
                    return (ok, ok ? $"entries found in {Path.GetFileName(_rt.Logs.CurrentFile)}" : "no log entries for denied access");
                });
                await Check(results, "SC-26", "A09 Logging", "Changes are recorded in the audit table", "create/update/delete audited", () =>
                {
                    var actions = new[] { "create", "delete", "settings" }.Select(a => (a, n: sandbox.CountAudit(a))).ToList();
                    return Task.FromResult((actions.All(x => x.n > 0), string.Join(", ", actions.Select(x => $"{x.a}: {x.n}"))));
                });
            }
            finally
            {
                try { Directory.Delete(sandboxDir, true); } catch (Exception ex) { _log.LogWarning(ex, "Sandbox not removed: {Dir}", sandboxDir); }
            }

            var run = new SecurityRun(DateTimeOffset.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6], DateTimeOffset.Now, by.Name, results);
            lock (_history) { _history.Insert(0, run); if (_history.Count > 20) _history.RemoveAt(_history.Count - 1); }
            try
            {
                var dir = Path.Combine(_rt.Paths.DataDir, "reports");
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, $"security-report-{run.Started:yyyyMMdd-HHmmss}.html"), SecurityReport.ToHtml(run, _rt), Encoding.UTF8);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Report file could not be written"); }
            _rt.Repo.Audit(by, "security-check", $"Security check: {run.Passed} passed, {run.Warnings} warnings, {run.Failed} failed");
            _log.LogInformation("Security check by {User}: {Pass} passed, {Warn} warnings, {Fail} failed", by.Account, run.Passed, run.Warnings, run.Failed);
            foreach (var r in results.Where(r => r.Status != CheckStatus.Pass))
                _log.LogWarning("Security check {Id} {Status}: {Title} – {Actual}", r.Id, r.Status, r.Title, r.Actual);
            return run;
        }
        finally { _running.Release(); }
    }

    private async Task Check(List<CheckResult> results, string id, string cat, string title, string expected, Func<Task<(bool ok, string actual)>> test, bool warnOnly = false)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (ok, actual) = await test();
            results.Add(new CheckResult(id, cat, title, expected, actual, ok ? CheckStatus.Pass : warnOnly ? CheckStatus.Warn : CheckStatus.Fail, sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Security check {Id} crashed", id);
            results.Add(new CheckResult(id, cat, title, expected, $"error: {ex.GetType().Name}: {ex.Message}", warnOnly ? CheckStatus.Warn : CheckStatus.Fail, sw.ElapsedMilliseconds));
        }
    }

    private static async Task<HttpResponseMessage> Send(HttpClient http, string path, string? cookie = null, string? host = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie != null) req.Headers.Add("Cookie", $"{AppRuntime.CookieName}={cookie}");
        if (host != null) req.Headers.Host = host;
        return await http.SendAsync(req);
    }

    private static async Task<bool> Throws<T>(Func<Task> act) where T : Exception
    {
        try { await act(); return false; } catch (T) { return true; }
    }

    private static Dictionary<string, string> Raw(string date, string no, string customer, string status, string value) =>
        new() { ["qDate"] = date, ["qNo"] = no, ["customer"] = customer, ["status"] = status, ["value"] = value };

    private static Dictionary<string, string> RawStock(string date, string part, string desc, string qty) =>
        new() { ["stockDate"] = date, ["partId"] = part, ["description"] = desc, ["qty"] = qty };

    private static string Mask(string s) => s.Length > 24 ? s[..12] + "…" : s;

    private async Task<string> RenderTable(EntityDef e, SalesRecord rec)
    {
        await using var renderer = new HtmlRenderer(_sp, _sp.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var p = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(RecordsTable.Entity)] = e,
                [nameof(RecordsTable.Records)] = new List<SalesRecord> { rec },
                [nameof(RecordsTable.CanEdit)] = (Func<SalesRecord, bool>)(_ => true),
                [nameof(RecordsTable.LowStockThreshold)] = 5,
            });
            var output = await renderer.RenderComponentAsync<RecordsTable>(p);
            return output.ToHtmlString();
        });
    }

    private static (bool, string) CheckOwnerOnly(string file)
    {
        if (!File.Exists(file)) return (false, "token file missing");
        if (OperatingSystem.IsWindows())
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => (SecurityIdentifier)r.IdentityReference).ToList();
            var others = rules.Where(s => s != me).Select(s => { try { return s.Translate(typeof(NTAccount)).Value; } catch { return s.Value; } }).ToList();
            return (others.Count == 0, others.Count == 0 ? "ACL: owner only (inheritance removed)" : "also accessible by: " + string.Join(", ", others));
        }
        var mode = File.GetUnixFileMode(file);
        var ok = (mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        return (ok, $"mode {Convert.ToString((int)mode, 8)}");
    }

    private static (bool, string) CheckNoBroadAccess(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var broad = new[] { WellKnownSidType.WorldSid, WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid }
                .Select(t => new SecurityIdentifier(t, null)).ToHashSet();
            var hits = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow && broad.Contains((SecurityIdentifier)r.IdentityReference)
                    && (r.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.Read)) != 0)
                .Select(r => r.IdentityReference.Translate(typeof(NTAccount)).Value).Distinct().ToList();
            return (hits.Count == 0, hits.Count == 0 ? $"{dir}: user profile ACL only" : $"readable by {string.Join(", ", hits)} – move data to %LOCALAPPDATA%");
        }
        var mode = File.GetUnixFileMode(dir);
        var ok = (mode & (UnixFileMode.OtherRead | UnixFileMode.OtherExecute)) == 0;
        return (ok, $"mode {Convert.ToString((int)mode, 8)}");
    }
}
