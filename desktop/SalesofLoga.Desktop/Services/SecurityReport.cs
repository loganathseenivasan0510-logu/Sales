using System.Net;
using System.Text;

namespace SalesofLoga.Desktop.Services;

/// <summary>Eigenständiger HTML-Bericht (ohne Skripte) – zum Vorzeigen, Archivieren oder Drucken als PDF.</summary>
public static class SecurityReport
{
    public static string ToHtml(SecurityRun run, AppRuntime rt)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var logo = rt.LogoDataUri;

        var logoTag = logo.Length > 0 ? "<img src=\"" + logo + "\" alt=\"centrotherm\">" : "";
        var started = E(run.Started.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        var verdict = run.Failed > 0 ? ("FAILED", "#a10a0c") : run.Warnings > 0 ? ("PASSED WITH WARNINGS", "#a35a00") : ("PASSED", "#1e7b3a");
        var sb = new StringBuilder();
        sb.Append($$"""
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Security Check – SalesofLoga – {{run.Started:yyyy-MM-dd HH:mm}}</title>
<style>
 body{font-family:"Segoe UI",system-ui,Arial,sans-serif;color:#1d1f23;margin:0;background:#f4f5f7}
 header{background:#c90c0e;color:#fff;padding:20px 32px;display:flex;align-items:center;gap:24px}
 header img{height:28px} header h1{font-size:20px;margin:0;font-weight:600}
 main{padding:24px 32px;max-width:1200px}
 .meta{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:8px 24px;font-size:13.5px;margin-bottom:20px}
 .meta b{display:block;color:#555b66;font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.03em}
 .tiles{display:flex;gap:16px;margin:16px 0 24px;flex-wrap:wrap}
 .tile{background:#fff;border:1px solid #e2e4e8;border-radius:10px;padding:14px 20px;min-width:150px}
 .tile span{display:block;font-size:12.5px;color:#555b66;font-weight:600}.tile strong{font-size:28px}
 .verdict{font-size:18px;font-weight:700;padding:10px 16px;border-radius:8px;color:#fff;display:inline-block}
 table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #e2e4e8;border-radius:10px;overflow:hidden;font-size:13.5px}
 th,td{padding:9px 10px;border-bottom:1px solid #e2e4e8;text-align:left;vertical-align:top}
 th{background:#fafafb;font-size:12px;text-transform:uppercase;letter-spacing:.03em;color:#555b66}
 .s{font-weight:700;border-radius:999px;padding:2px 10px;font-size:12px;white-space:nowrap}
 .Pass{background:#e6f4ea;color:#1e7b3a}.Warn{background:#fff3e0;color:#a35a00}.Fail{background:#fbe9e9;color:#a10a0c}
 .cat{color:#555b66;white-space:nowrap} td:first-child{white-space:nowrap} code{font-family:Consolas,monospace;font-size:12.5px;word-break:break-word}
 footer{color:#555b66;font-size:12.5px;margin-top:20px}
 @media print{body{background:#fff} header{-webkit-print-color-adjust:exact;print-color-adjust:exact} }
</style></head><body>
<header>{{logoTag}}<h1>Security Check – Sales Dashboard CT India</h1></header>
<main>
<div class="verdict" style="background:{{verdict.Item2}}">{{verdict.Item1}}</div>
<div class="tiles">
 <div class="tile"><span>Passed</span><strong>{{run.Passed}}</strong></div>
 <div class="tile"><span>Warnings</span><strong>{{run.Warnings}}</strong></div>
 <div class="tile"><span>Failed</span><strong>{{run.Failed}}</strong></div>
 <div class="tile"><span>Checks</span><strong>{{run.Results.Count}}</strong></div>
</div>
<div class="meta">
 <div><b>Run</b>{{started}}</div>
 <div><b>Started by</b>{{E(run.RunBy)}}</div>
 <div><b>App version</b>{{E(rt.Version)}} · .NET {{E(Environment.Version.ToString())}}</div>
 <div><b>Machine</b>{{E(Environment.MachineName)}} · {{E(System.Runtime.InteropServices.RuntimeInformation.OSDescription)}}</div>
 <div><b>Database</b><code>{{E(rt.Paths.DbPath)}}</code></div>
 <div><b>Run ID</b><code>{{E(run.Id)}}</code></div>
</div>
<table><thead><tr><th>ID</th><th>OWASP category</th><th>Check</th><th>Expected</th><th>Actual</th><th>Result</th></tr></thead><tbody>
""");
        foreach (var r in run.Results)
            sb.Append($"<tr><td><code>{E(r.Id)}</code></td><td class=\"cat\">{E(r.Category)}</td><td>{E(r.Title)}</td><td>{E(r.Expected)}</td><td><code>{E(r.Actual)}</code></td><td><span class=\"s {r.Status}\">{r.Status.ToString().ToUpperInvariant()}</span><br><small>{r.Ms} ms</small></td></tr>\n");
        sb.Append("""
</tbody></table>
<footer>Checks run against the running instance (HTTP on 127.0.0.1) and a temporary sandbox database – productive data is not modified.
Categories: OWASP Top 10 (2021). WARN = recommendation, FAIL = must be fixed before use.</footer>
</main></body></html>
""");
        return sb.ToString();
    }
}
