<#
.SYNOPSIS
  Externe Black-Box-Sicherheitstests für SalesofLoga.exe – mit HTML-Bericht zum Vorzeigen.

.DESCRIPTION
  Startet die EXE mit einem Wegwerf-Datenordner und präparierten Testdaten (XSS-, Formel- und
  Upload-Angriffe aus tests/fixtures/webdata), greift dann wie ein Angreifer von außen per HTTP zu
  und prüft Betriebssystem-Eigenschaften (lauschende Ports, Dateirechte, Datenbankformat).
  Zusätzlich wird der eingebaute Security Check (--security-check, 26 Prüfungen) ausgeführt und verlinkt.
  Die echten Daten des Benutzers werden nicht berührt.

.EXAMPLE
  .\Invoke-SecurityTests.ps1 -ExePath ..\..\publish\SalesofLoga.exe
  .\Invoke-SecurityTests.ps1 -ExePath .\SalesofLoga.exe -OutFile .\security-tests.html -Open

.NOTES
  PowerShell 7+ (pwsh) empfohlen; Windows PowerShell 5.1 funktioniert ebenfalls.
  Exit-Code: 0 = alles bestanden, 1 = mindestens ein Test fehlgeschlagen.
#>
[CmdletBinding()]
param(
  [string]$ExePath = (Join-Path $PSScriptRoot '..\..\publish\SalesofLoga.exe'),
  [string]$OutFile = (Join-Path (Get-Location) 'security-tests-external.html'),
  [string]$Fixtures = (Join-Path $PSScriptRoot '..\fixtures\webdata'),
  [switch]$Open,
  [switch]$KeepData
)

$ErrorActionPreference = 'Stop'
$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or $IsWindows
$ExePath = (Resolve-Path $ExePath).Path
$Fixtures = (Resolve-Path $Fixtures).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("slg-sectest-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$dataDir = Join-Path $work 'data'
$internalDir = Join-Path $work 'internal'
New-Item -ItemType Directory -Force -Path $dataDir, $internalDir | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$started = Get-Date

function Write-Step($msg) { Write-Host "  » $msg" -ForegroundColor DarkGray }

function Add-Result([string]$Id, [string]$Category, [string]$Title, [string]$Expected, [scriptblock]$Test) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $r = & $Test
    $ok = [bool]$r[0]; $actual = [string]$r[1]
  } catch {
    $ok = $false; $actual = "error: $($_.Exception.Message)"
  }
  $status = if ($ok) { 'PASS' } else { 'FAIL' }
  $results.Add([pscustomobject]@{ Id = $Id; Category = $Category; Title = $Title; Expected = $Expected; Actual = $actual; Status = $status; Ms = $sw.ElapsedMilliseconds })
  $color = if ($ok) { 'Green' } else { 'Red' }
  Write-Host ("{0,-7} {1,-5} {2}" -f $Id, $status, $Title) -ForegroundColor $color
  if (-not $ok) { Write-Host "        → $actual" -ForegroundColor Red }
}

# Rohe HTTP-Anfrage (eigene Header wie Host/Cookie, keine automatische Weiterleitung)
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseCookies = $false
$handler.AllowAutoRedirect = $false
$http = [System.Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(15)

function Invoke-Raw([string]$Path, [string]$Cookie, [string]$HostHeader) {
  $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$script:base$Path")
  if ($Cookie) { [void]$req.Headers.TryAddWithoutValidation('Cookie', "slg_session=$Cookie") }
  if ($HostHeader) { $req.Headers.Host = $HostHeader }
  $res = $http.SendAsync($req).GetAwaiter().GetResult()
  $bytes = $res.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
  $headers = @{}
  foreach ($h in $res.Headers) { $headers[$h.Key] = ($h.Value -join ', ') }
  foreach ($h in $res.Content.Headers) { $headers[$h.Key] = ($h.Value -join ', ') }
  [pscustomobject]@{ Status = [int]$res.StatusCode; Headers = $headers; Bytes = $bytes; Text = [Text.Encoding]::UTF8.GetString($bytes) }
}

Write-Host "`nSalesofLoga – external security tests" -ForegroundColor White
Write-Host "EXE:  $ExePath"
Write-Host "Data: $dataDir (temporary)`n"

# ---------------------------------------------------------------- 1. Eingebauter Security Check
Write-Step 'Running built-in security check (--security-check)'
$internalReport = Join-Path $internalDir 'security-check-internal.html'
$p = Start-Process -FilePath $ExePath -ArgumentList @('--security-check', '--data', "`"$internalDir`"", '--import', "`"$Fixtures`"", '--report', "`"$internalReport`"") -PassThru -Wait -NoNewWindow
$internalExit = $p.ExitCode

# ---------------------------------------------------------------- 2. App starten (wie ein Benutzer)
Write-Step 'Starting app with test data'
$proc = Start-Process -FilePath $ExePath -ArgumentList @('--no-browser', '--data', "`"$dataDir`"", '--import', "`"$Fixtures`"") -PassThru -NoNewWindow
try {
  $instance = Join-Path $dataDir 'instance.json'
  $deadline = (Get-Date).AddSeconds(60)
  while (-not (Test-Path $instance) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
  if (-not (Test-Path $instance)) { throw "App did not start (no instance.json). See $dataDir\logs" }
  $info = Get-Content $instance -Raw | ConvertFrom-Json
  $token = $info.token
  $script:base = "http://127.0.0.1:$($info.port)"
  for ($i = 0; $i -lt 40; $i++) { try { if ((Invoke-Raw '/health').Status -eq 200) { break } } catch { Start-Sleep -Milliseconds 250 } }
  Write-Step "App runs on $script:base (PID $($proc.Id))`n"

  # ============================ A01 Broken Access Control
  Add-Result 'EXT-01' 'A01 Access Control' 'Page without session cookie is rejected' 'HTTP 401' {
    $r = Invoke-Raw '/quotation'; @(($r.Status -eq 401), "HTTP $($r.Status)") }
  Add-Result 'EXT-02' 'A01 Access Control' 'Login link with wrong token is rejected' 'HTTP 401' {
    $r = Invoke-Raw '/auth?k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'; @(($r.Status -eq 401), "HTTP $($r.Status)") }
  Add-Result 'EXT-03' 'A01 Access Control' 'Database backup cannot be downloaded without session' 'HTTP 401' {
    $r = Invoke-Raw '/backup'; @(($r.Status -eq 401), "HTTP $($r.Status)") }
  Add-Result 'EXT-04' 'A01 Access Control' 'CSV export cannot be downloaded without session' 'HTTP 401' {
    $r = Invoke-Raw '/export/quotation.csv'; @(($r.Status -eq 401), "HTTP $($r.Status)") }
  Add-Result 'EXT-05' 'A01 Access Control' 'Path traversal on attachment URL' 'HTTP 404' {
    $a = Invoke-Raw '/attachments/..%2f..%2fsalesofloga.db' $token
    $b = Invoke-Raw '/attachments/..\..\salesofloga.db' $token
    @((($a.Status -eq 404) -and ($b.Status -in 400, 404)), "HTTP $($a.Status) / $($b.Status)") }

  # ============================ A02 Cryptographic Failures
  Add-Result 'EXT-06' 'A02 Cryptography' 'Session token has ≥ 256 bit' '≥ 43 Base64url chars' {
    @(($token.Length -ge 43 -and $token -match '^[A-Za-z0-9_-]+$'), "$($token.Length) chars") }
  Add-Result 'EXT-07' 'A02 Cryptography' 'Token file (instance.json) is readable only by the owner' 'owner only' {
    if ($onWindows) {
      $acl = Get-Acl $instance
      $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
      $others = @($acl.Access | Where-Object { $_.AccessControlType -eq 'Allow' } | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]) } | Where-Object { $_ -ne $me })
      @(($others.Count -eq 0 -and $acl.AreAccessRulesProtected), "inheritance removed: $($acl.AreAccessRulesProtected); others: $(@($others).Count)")
    } else {
      $mode = (& stat -c '%a' $instance); @(($mode -eq '600'), "mode $mode")
    } }

  # ============================ A03 Injection
  Add-Result 'EXT-08' 'A03 Injection' 'Stored XSS from imported data is HTML-encoded in the page' '&lt;img … no raw <img onerror>' {
    $r = Invoke-Raw '/stock' $token
    $raw = $r.Text -match '<img src=x onerror'
    $enc = $r.Text -match '&lt;img src=x onerror'
    @(((-not $raw) -and $enc), "raw markup: $raw, encoded: $enc") }
  Add-Result 'EXT-09' 'A03 Injection' 'CSV export neutralises Excel formulas (=HYPERLINK)' "cell starts with '" {
    $r = Invoke-Raw '/export/stock.csv' $token
    $ok = $r.Text -match "'=HYPERLINK" -and $r.Text -notmatch '(^|;)=HYPERLINK'
    @($ok, ($r.Text -split "`n" | Where-Object { $_ -match 'HYPERLINK' } | Select-Object -First 1).Trim()) }
  Add-Result 'EXT-10' 'A03 Injection' "SQL injection in export filter (' OR '1'='1)" 'only header row' {
    $q = [Uri]::EscapeDataString("' OR '1'='1")
    $r = Invoke-Raw "/export/quotation.csv?qNo=$q" $token
    $lines = @($r.Text.Trim() -split "`r?`n").Count
    @(($r.Status -eq 200 -and $lines -eq 1), "HTTP $($r.Status), $lines line(s)") }
  Add-Result 'EXT-11' 'A03 Injection' 'Host header spoofing / DNS rebinding' 'HTTP 400' {
    $r = Invoke-Raw '/' $token 'attacker.example'; @(($r.Status -eq 400), "HTTP $($r.Status)") }

  # ============================ A04 Insecure Design
  Add-Result 'EXT-12' 'A04 Insecure Design' 'Executable disguised as PDF was not imported' '1 of 2 PDFs stored' {
    $r = Invoke-Raw '/quotation' $token
    $links = ([regex]::Matches($r.Text, 'attachments/[0-9a-f-]{36}')).Count
    @(($links -eq 1), "$links attachment link(s) on page") }
  Add-Result 'EXT-13' 'A04 Insecure Design' 'Attachment is served with fixed type + nosniff' 'application/pdf, nosniff' {
    $page = Invoke-Raw '/quotation' $token
    $id = ([regex]::Match($page.Text, 'attachments/([0-9a-f-]{36})')).Groups[1].Value
    $r = Invoke-Raw "/attachments/$id" $token
    $ct = $r.Headers['Content-Type']; $ns = $r.Headers['X-Content-Type-Options']
    $magic = [Text.Encoding]::ASCII.GetString($r.Bytes, 0, [Math]::Min(5, $r.Bytes.Length))
    @(($ct -eq 'application/pdf' -and $ns -eq 'nosniff' -and $magic -eq '%PDF-'), "$ct, $ns, starts with $magic") }

  # ============================ A05 Security Misconfiguration
  Add-Result 'EXT-14' 'A05 Misconfiguration' 'Process listens on 127.0.0.1 only' 'no 0.0.0.0 / LAN listener' {
    if ($onWindows) {
      $l = @(Get-NetTCPConnection -OwningProcess $proc.Id -State Listen -ErrorAction SilentlyContinue)
      $bad = @($l | Where-Object { $_.LocalAddress -notin '127.0.0.1', '::1' })
      @(($l.Count -gt 0 -and $bad.Count -eq 0), (($l | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', '))
    } else {
      # Linux/macOS-Testlauf: lauschende Sockets aus /proc/net/tcp (Status 0A = LISTEN)
      $portHex = '{0:X4}' -f [int]$info.port
      $l = @(Get-Content /proc/net/tcp, /proc/net/tcp6 -ErrorAction SilentlyContinue | Where-Object { $_ -match ":$portHex\s+[0-9A-F:]+\s+0A\s" } |
          ForEach-Object { ($_.Trim() -split '\s+')[1] })
      $bad = @($l | Where-Object { $_ -notmatch '^0100007F:' -and $_ -notmatch '^0{24}01000000:' })
      @(($l.Count -gt 0 -and $bad.Count -eq 0), (($l | ForEach-Object { if ($_ -like '0100007F:*') { "127.0.0.1:$($info.port)" } else { $_ } }) -join ', '))
    } }
  Add-Result 'EXT-15' 'A05 Misconfiguration' 'Security headers on every page' 'CSP, nosniff, DENY, no-referrer, no Server' {
    $r = Invoke-Raw '/' $token
    $h = $r.Headers; $missing = @()
    if ($h['Content-Security-Policy'] -notmatch "script-src 'self'" -or $h['Content-Security-Policy'] -match 'unsafe') { $missing += 'CSP' }
    if ($h['Content-Security-Policy'] -notmatch "frame-ancestors 'none'") { $missing += 'frame-ancestors' }
    if ($h['X-Content-Type-Options'] -ne 'nosniff') { $missing += 'nosniff' }
    if ($h['X-Frame-Options'] -ne 'DENY') { $missing += 'X-Frame-Options' }
    if ($h['Referrer-Policy'] -ne 'no-referrer') { $missing += 'Referrer-Policy' }
    if ($h.ContainsKey('Server')) { $missing += 'Server header' }
    @(($missing.Count -eq 0), $(if ($missing) { 'missing: ' + ($missing -join ', ') } else { 'all present' })) }
  Add-Result 'EXT-16' 'A05 Misconfiguration' 'Session cookie flags' 'HttpOnly, SameSite=Strict' {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$script:base/auth?k=$token")
    $res = $http.SendAsync($req).GetAwaiter().GetResult()
    $c = ($res.Headers.GetValues('Set-Cookie') -join ' ')
    @(($c -match 'httponly' -and $c -match 'samesite=strict'), (($c -replace 'slg_session=[^;]+', 'slg_session=***'))) }

  # ============================ A08 Data Integrity
  Add-Result 'EXT-17' 'A08 Data Integrity' 'Database is a standard SQLite file with app id' 'SQLite format 3 + 0x534C4741' {
    $fs = [IO.File]::Open((Join-Path $dataDir 'salesofloga.db'), 'Open', 'Read', 'ReadWrite')
    try { $hdr = New-Object byte[] 100; [void]$fs.Read($hdr, 0, 100) } finally { $fs.Dispose() }
    $magic = [Text.Encoding]::ASCII.GetString($hdr, 0, 15)
    $appId = ([int]$hdr[68] -shl 24) -bor ([int]$hdr[69] -shl 16) -bor ([int]$hdr[70] -shl 8) -bor [int]$hdr[71]
    @(($magic -eq 'SQLite format 3' -and $appId -eq 0x534C4741), "$magic, application_id 0x$('{0:X8}' -f $appId)") }
  Add-Result 'EXT-18' 'A08 Data Integrity' 'Backup download (with session) is a valid SQLite DB' 'SQLite header' {
    $r = Invoke-Raw '/backup' $token
    $magic = [Text.Encoding]::ASCII.GetString($r.Bytes, 0, [Math]::Min(15, $r.Bytes.Length))
    @(($r.Status -eq 200 -and $magic -eq 'SQLite format 3'), "HTTP $($r.Status), $($r.Bytes.Length) bytes, $magic") }

  # ============================ A09 Logging
  Add-Result 'EXT-19' 'A09 Logging' 'Attacks are visible in the log file' 'blocked host + rejected upload logged' {
    Start-Sleep -Milliseconds 500
    $log = Get-Content (Join-Path $dataDir 'logs\*.log') -Raw
    $a = $log -match 'Blocked request with foreign Host header'; $b = $log -match 'Attachment rejected'
    @(($a -and $b), "blocked host: $a, rejected upload: $b") }

  # ============================ Eingebauter Check
  Add-Result 'EXT-20' 'Built-in check' 'Built-in security check (26 checks) passes' 'exit code 0' {
    @(($internalExit -eq 0 -and (Test-Path $internalReport)), "exit code $internalExit") }
}
finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit(5000) | Out-Null }
}

# ---------------------------------------------------------------- Bericht
$pass = @($results | Where-Object Status -eq 'PASS').Count
$fail = @($results | Where-Object Status -eq 'FAIL').Count
$verdict = if ($fail -gt 0) { 'FAILED', '#a10a0c' } else { 'PASSED', '#1e7b3a' }
$enc = { param($s) [System.Net.WebUtility]::HtmlEncode([string]$s) }
$exeHash = (Get-FileHash $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
$exeVer = (Get-Item $ExePath).VersionInfo.ProductVersion
$rows = ($results | ForEach-Object {
    "<tr><td><code>$(& $enc $_.Id)</code></td><td class='cat'>$(& $enc $_.Category)</td><td>$(& $enc $_.Title)</td><td>$(& $enc $_.Expected)</td><td><code>$(& $enc $_.Actual)</code></td><td><span class='s $($_.Status)'>$($_.Status)</span><br><small>$($_.Ms) ms</small></td></tr>"
  }) -join "`n"
$internalCopy = [IO.Path]::ChangeExtension($OutFile, $null).TrimEnd('.') + '-builtin.html'
if (Test-Path $internalReport) { Copy-Item $internalReport $internalCopy -Force }

$html = @"
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>External security tests – SalesofLoga</title>
<style>
body{font-family:"Segoe UI",system-ui,Arial,sans-serif;color:#1d1f23;margin:0;background:#f4f5f7}
header{background:#c90c0e;color:#fff;padding:20px 32px}header h1{margin:0;font-size:20px;font-weight:600}
main{padding:24px 32px;max-width:1200px}.verdict{display:inline-block;color:#fff;font-weight:700;font-size:18px;padding:10px 16px;border-radius:8px}
.tiles{display:flex;gap:16px;margin:16px 0 20px}.tile{background:#fff;border:1px solid #e2e4e8;border-radius:10px;padding:14px 20px;min-width:150px}
.tile span{display:block;font-size:12.5px;color:#555b66;font-weight:600}.tile strong{font-size:28px}
.meta{font-size:13.5px;margin-bottom:18px;line-height:1.7}.meta b{color:#555b66}
table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #e2e4e8;font-size:13.5px}
th,td{padding:9px 10px;border-bottom:1px solid #e2e4e8;text-align:left;vertical-align:top}td:first-child{white-space:nowrap}
th{background:#fafafb;font-size:12px;text-transform:uppercase;color:#555b66}.cat{color:#555b66;white-space:nowrap}
code{font-family:Consolas,monospace;font-size:12.5px;word-break:break-word}.s{font-weight:700;border-radius:999px;padding:2px 10px;font-size:12px}
.PASS{background:#e6f4ea;color:#1e7b3a}.FAIL{background:#fbe9e9;color:#a10a0c}footer{color:#555b66;font-size:12.5px;margin-top:18px}
</style></head><body><header><h1>External security tests (black box) – SalesofLoga</h1></header><main>
<div class="verdict" style="background:$($verdict[1])">$($verdict[0])</div>
<div class="tiles"><div class="tile"><span>Passed</span><strong>$pass</strong></div><div class="tile"><span>Failed</span><strong>$fail</strong></div><div class="tile"><span>Tests</span><strong>$($results.Count)</strong></div></div>
<div class="meta"><b>Run:</b> $(& $enc $started.ToString('yyyy-MM-dd HH:mm:ss')) &nbsp; <b>Machine:</b> $(& $enc ([Environment]::MachineName)) &nbsp; <b>User:</b> $(& $enc ([Environment]::UserName))<br>
<b>EXE:</b> <code>$(& $enc $ExePath)</code> &nbsp; <b>Version:</b> $(& $enc $exeVer)<br><b>SHA-256:</b> <code>$exeHash</code><br>
<b>Built-in check report:</b> <code>$(& $enc (Split-Path $internalCopy -Leaf))</code></div>
<table><thead><tr><th>ID</th><th>OWASP category</th><th>Test</th><th>Expected</th><th>Actual</th><th>Result</th></tr></thead><tbody>
$rows
</tbody></table>
<footer>Black-box tests from outside the process against a temporary data folder with prepared attack data (XSS, CSV formula, disguised executable, path traversal). Productive data is not touched. Script: tests/security/Invoke-SecurityTests.ps1</footer>
</main></body></html>
"@
[IO.File]::WriteAllText($OutFile, $html, [Text.UTF8Encoding]::new($false))
Write-Host "`n$pass passed, $fail failed → $OutFile" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
if (-not $KeepData) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host "Test data kept: $work" }
if ($Open) { Start-Process $OutFile }
exit $(if ($fail -gt 0) { 1 } else { 0 })
