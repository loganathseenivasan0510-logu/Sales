using System.Text;
using SalesofLoga.Desktop.Data;
using SalesofLoga.Desktop.Infrastructure;

namespace SalesofLoga.Desktop.Services;

/// <summary>
/// SalesofLoga.exe --selftest : prüft die Datenbank-Schicht auf DIESEM Rechner (winsqlite3, WAL, BLOBs, Backup)
/// mit einer Wegwerf-Datenbank. Exit-Code 0 = OK. Ergebnis steht im Log (logs\salesofloga-*.log).
/// </summary>
public static class SelfTest
{
    public static int Run(SalesRepository repo, AppUser user, ILogger log)
    {
        var steps = new List<(string, bool, string)>();
        void Step(string name, Func<string> act)
        {
            try { steps.Add((name, true, act())); }
            catch (Exception ex) { steps.Add((name, false, ex.Message)); log.LogError(ex, "Selftest step {Step} failed", name); }
        }
        try
        {
            Step("sqlite library", () => SqliteConnection.LibraryVersion);
            Step("create schema", () => { repo.Initialize(); return "ok"; });
            string? id = null;
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj<<>>endobj\n%%EOF\n");
            Step("insert with BLOB", () =>
            {
                var r = repo.CreateAsync(Schema.Quotation, new Dictionary<string, string> { ["qDate"] = "2026-11-25", ["qNo"] = "SELFTEST-1", ["customer"] = "EMMVEE", ["status"] = "Open", ["value"] = "1234.56" },
                    "INR", new NewAttachment("selftest.pdf", "application/pdf", pdf), user).GetAwaiter().GetResult();
                id = r.Id;
                return $"{r.Id} value={r.Money("value")}";
            });
            Step("read BLOB back", () =>
            {
                var rec = repo.Get(Schema.Quotation, id!)!;
                var a = repo.GetAttachment(rec.Attachment!.Id)!.Value;
                if (!a.Data.SequenceEqual(pdf)) throw new Exception("BLOB mismatch");
                return $"{a.Data.Length} bytes identical";
            });
            Step("unicode round trip", () =>
            {
                var r = repo.CreateAsync(Schema.Stock, new Dictionary<string, string> { ["stockDate"] = "2026-11-25", ["partId"] = "SELF-UNI", ["description"] = "Quarzrohr ü ä ö ß – ₹ €", ["qty"] = "3" }, null, null, user).GetAwaiter().GetResult();
                if (repo.Get(Schema.Stock, r.Id)!.Text("description") != "Quarzrohr ü ä ö ß – ₹ €") throw new Exception("text mismatch");
                return "ok";
            });
            Step("update + audit", () =>
            {
                repo.UpdateAsync(Schema.Quotation, id!, new Dictionary<string, string> { ["qDate"] = "2026-11-25", ["qNo"] = "SELFTEST-1", ["customer"] = "EMMVEE", ["status"] = "Closed", ["value"] = "1234.56" }, "INR", null, false, user).GetAwaiter().GetResult();
                return $"audit updates: {repo.CountAudit("update")}";
            });
            Step("soft delete", () => { repo.DeleteAsync(Schema.Quotation, id!, user).GetAwaiter().GetResult(); return $"remaining: {repo.CountRecords(false)}"; });
            Step("backup api", () =>
            {
                var dest = repo.DbPath + ".bak";
                repo.BackupTo(dest);
                var size = new FileInfo(dest).Length;
                File.Delete(dest);
                return $"{size} bytes";
            });
            Step("integrity", () => { var (ic, fk) = repo.CheckIntegrity(); if (ic != "ok" || fk != 0) throw new Exception($"{ic}/{fk}"); return "ok"; });
        }
        finally
        {
            foreach (var f in new[] { repo.DbPath, repo.DbPath + "-wal", repo.DbPath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
        foreach (var (n, ok, info) in steps)
        {
            log.LogInformation("SELFTEST {Result} {Step}: {Info}", ok ? "PASS" : "FAIL", n, info);
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {n}: {info}");
        }
        return steps.All(s => s.Item2) ? 0 : 1;
    }
}
