using System.Text;
using SalesofLoga.Desktop.Data;

namespace SalesofLoga.Desktop.Services;

/// <summary>HTTP-Endpunkte neben der Blazor-Oberfläche (alle hinter LocalAccessMiddleware).</summary>
public static class Endpoints
{
    private static readonly HashSet<string> InlineMimes = new() { "application/pdf", "image/png", "image/jpeg", "image/webp" };

    public static void Map(WebApplication app)
    {
        app.MapGet("/attachments/{id:guid}", (Guid id, SalesRepository repo, HttpContext ctx) =>
        {
            var a = repo.GetAttachment(id.ToString());
            if (a == null) return Results.NotFound();
            var (info, data) = a.Value;
            // Ausgeliefert wird nur, was die Signaturprüfung als PDF/Bild erkannt hat – nie HTML/Skript
            var mime = Validator.DetectMime(data);
            if (mime == null || !InlineMimes.Contains(mime)) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            ctx.Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(info.FileName)}";
            // Kein CSP auf der Datei selbst: Chromes PDF-Viewer wird sonst blockiert. Schutz kommt hier aus
            // Signaturprüfung + festem MIME-Typ + nosniff (es kann nie HTML ausgeliefert werden).
            ctx.Response.Headers.Remove("Content-Security-Policy");
            return Results.Bytes(data, mime);
        });

        app.MapGet("/export/{slug}.csv", (string slug, SalesRepository repo, HttpContext ctx, AppRuntime rt, ILoggerFactory lf) =>
        {
            var e = Schema.BySlug(slug);
            if (e == null) return Results.NotFound();
            var filters = ctx.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
            var csv = repo.ExportCsv(e, filters);
            lf.CreateLogger("Export").LogInformation("{User} exported {Entity} as CSV", rt.User.Account, e.Key);
            return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"{e.Table}-{DateTime.Now:yyyy-MM-dd}.csv");
        });

        app.MapGet("/backup", (SalesRepository repo, AppRuntime rt, ILoggerFactory lf) =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"salesofloga-{Guid.NewGuid():N}.db");
            repo.BackupTo(tmp);
            var bytes = File.ReadAllBytes(tmp);
            File.Delete(tmp);
            repo.Audit(rt.User, "backup-download", "Database backup downloaded");
            lf.CreateLogger("Backup").LogInformation("{User} downloaded a database backup ({Bytes} bytes)", rt.User.Account, bytes.Length);
            return Results.File(bytes, "application/vnd.sqlite3", $"salesofloga-backup-{DateTime.Now:yyyy-MM-dd-HHmm}.db");
        });

        app.MapGet("/security/report/{id}", (string id, SecurityCheckService svc, AppRuntime rt, HttpContext ctx) =>
        {
            var run = svc.History.FirstOrDefault(r => r.Id == id);
            if (run == null) return Results.NotFound();
            // Bericht ist eigenständiges HTML mit Inline-CSS und ohne Skripte
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; img-src data:";
            var download = ctx.Request.Query.ContainsKey("download");
            if (download) ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"security-report-{run.Started:yyyyMMdd-HHmmss}.html\"";
            return Results.Content(SecurityReport.ToHtml(run, rt), "text/html; charset=utf-8");
        });
    }
}
