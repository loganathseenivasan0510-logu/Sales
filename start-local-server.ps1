<#
  Startet einen minimalen Webserver für SalesofLoga auf http://localhost:8080/
  - nur für Test/Entwicklung (SharePoint-Anmeldung braucht http(s), file:// reicht nicht)
  - lauscht ausschließlich auf localhost, liefert nur Dateien aus diesem Ordner aus
  Aufruf:  Rechtsklick -> "Mit PowerShell ausführen"  oder  powershell -ExecutionPolicy Bypass -File .\start-local-server.ps1
#>
param([int]$Port = 8080)

$root = (Resolve-Path $PSScriptRoot).Path
$mime = @{
  ".html" = "text/html; charset=utf-8"; ".js" = "text/javascript; charset=utf-8"; ".css" = "text/css; charset=utf-8"
  ".json" = "application/json"; ".png" = "image/png"; ".ico" = "image/x-icon"; ".svg" = "image/svg+xml"; ".txt" = "text/plain; charset=utf-8"
}

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "SalesofLoga läuft auf http://localhost:$Port/  (Strg+C beendet)" -ForegroundColor Green
Start-Process "http://localhost:$Port/index.html"

try {
  while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $res = $ctx.Response
    try {
      $rel = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath.TrimStart("/"))
      if ([string]::IsNullOrEmpty($rel)) { $rel = "index.html" }
      $full = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $rel))
      if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path $full -PathType Leaf)) {
        $res.StatusCode = 404
      } else {
        $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
        $res.ContentType = $(if ($mime.ContainsKey($ext)) { $mime[$ext] } else { "application/octet-stream" })
        $res.Headers.Add("X-Content-Type-Options", "nosniff")
        $res.Headers.Add("Cache-Control", "no-cache")
        $bytes = [IO.File]::ReadAllBytes($full)
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
      }
    } catch { $res.StatusCode = 500 }
    finally { $res.Close() }
  }
} finally {
  $listener.Stop()
}
