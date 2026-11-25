namespace SalesofLoga.Desktop.Services;

/// <summary>
/// Ablageorte. Standard (Windows): %LOCALAPPDATA%\SalesofLoga
///   salesofloga.db        – Datenbank (SQLite, Anhänge als BLOB)
///   backups\              – tägliche Sicherungen (30 Stück werden behalten)
///   logs\                 – Logdateien (14 Tage)
///   instance.json         – Port/Token der laufenden Instanz (nur für den eigenen Benutzer lesbar)
/// Mit --data "Pfad" lässt sich ein anderer Ordner verwenden.
/// </summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string DbPath => Path.Combine(DataDir, "salesofloga.db");
    public string BackupDir => Path.Combine(DataDir, "backups");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string InstanceFile => Path.Combine(DataDir, "instance.json");

    public AppPaths(string? overrideDir)
    {
        DataDir = string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "SalesofLoga")
            : Path.GetFullPath(overrideDir);
        Directory.CreateDirectory(DataDir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(LogDir);
    }
}
