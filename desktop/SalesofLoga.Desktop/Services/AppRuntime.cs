using System.Reflection;
using System.Security.Cryptography;
using SalesofLoga.Desktop.Data;

namespace SalesofLoga.Desktop.Services;

/// <summary>Laufzeit-Kontext der Instanz: Pfade, Benutzer, Sitzungs-Token, Port.</summary>
public sealed class AppRuntime
{
    public const string CookieName = "slg_session";

    public AppPaths Paths { get; }
    public AppUser User { get; }
    public SalesRepository Repo { get; }
    public FileLoggerProvider Logs { get; }
    public CommandLine Options { get; }
    /// <summary>256 Bit aus dem kryptografischen Zufallsgenerator, je Programmstart neu.</summary>
    public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public int Port { get; set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public string Version { get; } = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string LoginUrl => $"{BaseUrl}/auth?k={Token}";

    public AppRuntime(AppPaths paths, AppUser user, SalesRepository repo, FileLoggerProvider logs, CommandLine options)
    {
        Paths = paths; User = user; Repo = repo; Logs = logs; Options = options;
    }

    public bool IsAdmin => Repo.IsAdmin(User, Repo.GetMaster());

    /// <summary>Logo als data:-URI für den eigenständigen HTML-Bericht (wird beim Start gesetzt).</summary>
    public string LogoDataUri { get; set; } = "";
}

public sealed class CommandLine
{
    public string? DataDir { get; init; }
    public int Port { get; init; }
    public bool NoBrowser { get; init; }
    public bool Stay { get; init; }
    public bool SelfTest { get; init; }
    public bool SecurityCheck { get; init; }
    public string? ReportPath { get; init; }
    public string? ImportDir { get; init; }

    public static CommandLine Parse(string[] args)
    {
        string? Val(string name) { var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return new CommandLine
        {
            DataDir = Val("--data"),
            Port = int.TryParse(Val("--port"), out var p) ? p : 0,
            NoBrowser = Flag("--no-browser"),
            Stay = Flag("--stay") || Flag("--no-browser"),
            SelfTest = Flag("--selftest"),
            SecurityCheck = Flag("--security-check"),
            ReportPath = Val("--report"),
            ImportDir = Val("--import"),
        };
    }
}
