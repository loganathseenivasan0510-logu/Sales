using System.Security.Cryptography;
using System.Text;

namespace SalesofLoga.Desktop.Services;

/// <summary>
/// Schützt den lokalen Webserver:
/// 1. Host-Header muss 127.0.0.1/localhost sein (Schutz vor DNS-Rebinding aus dem Browser)
/// 2. Zugriff nur mit Sitzungs-Cookie = zufälliges Token dieses Programmstarts
///    (andere Benutzer/Prozesse auf demselben PC oder Terminalserver kommen nicht an die Daten)
/// 3. Sicherheits-Header auf jeder Antwort
/// </summary>
public sealed class LocalAccessMiddleware
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "[::1]" };
    private readonly RequestDelegate _next;
    private readonly AppRuntime _rt;
    private readonly ILogger<LocalAccessMiddleware> _log;

    public LocalAccessMiddleware(RequestDelegate next, AppRuntime rt, ILogger<LocalAccessMiddleware> log)
    {
        _next = next; _rt = rt; _log = log;
    }

    public static string ContentSecurityPolicy(int port) =>
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
        $"connect-src 'self' ws://127.0.0.1:{port} ws://localhost:{port}; font-src 'self'; object-src 'none'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), usb=()";
        h["Content-Security-Policy"] = ContentSecurityPolicy(_rt.Port);
        h["Cache-Control"] = "no-store";

        if (!AllowedHosts.Contains(ctx.Request.Host.Host))
        {
            _log.LogWarning("Blocked request with foreign Host header '{Host}' from {Ip}", ctx.Request.Host.Value, ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Bad host");
            return;
        }

        var path = ctx.Request.Path.Value ?? "/";
        if (path == "/health") { await ctx.Response.WriteAsync("ok"); return; }

        if (path == "/auth")
        {
            if (Matches(ctx.Request.Query["k"].ToString()))
            {
                ctx.Response.Cookies.Append(AppRuntime.CookieName, _rt.Token, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true, Secure = false
                });
                _log.LogInformation("Browser session opened from {Ip}", ctx.Connection.RemoteIpAddress);
                ctx.Response.Redirect("/");
                return;
            }
            _log.LogWarning("Invalid login token from {Ip}", ctx.Connection.RemoteIpAddress);
            await Deny(ctx);
            return;
        }

        if (!Matches(ctx.Request.Cookies[AppRuntime.CookieName]))
        {
            await Deny(ctx);
            return;
        }
        await _next(ctx);
    }

    private bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var a = Encoding.UTF8.GetBytes(candidate);
        var b = Encoding.UTF8.GetBytes(_rt.Token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task Deny(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>SalesofLoga</title></head><body>" +
            "<h1>Session expired</h1><p>Please start <strong>SalesofLoga.exe</strong> again – it opens the app automatically.</p></body></html>");
    }
}
