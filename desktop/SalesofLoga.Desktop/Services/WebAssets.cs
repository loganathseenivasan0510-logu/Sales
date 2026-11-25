using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace SalesofLoga.Desktop.Services;

/// <summary>
/// Liefert das Verzeichnis mit wwwroot\ und dem Endpoint-Manifest.
/// - Entwicklung / normaler Publish: neben der Anwendung
/// - Single-File-EXE: eingebettetes webassets.zip wird einmalig nach %LOCALAPPDATA%\SalesofLoga\app\&lt;hash&gt;\ entpackt
/// </summary>
public static class WebAssets
{
    public const string ManifestName = "SalesofLoga.staticwebassets.endpoints.json";

    public static string ResolveContentRoot(AppPaths paths, ILogger log)
    {
        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, ManifestName))) return baseDir;

        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("webassets.zip");
        if (res == null)
        {
            log.LogWarning("No web assets found next to the app and none embedded – UI files may be missing");
            return baseDir;
        }
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var hash = Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16].ToLowerInvariant();
        var target = Path.Combine(paths.AppCacheDir, hash);
        var marker = Path.Combine(target, ".complete");
        if (File.Exists(marker)) return target;

        var tmp = target + ".tmp-" + Environment.ProcessId;
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ms.Position = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var dest = Path.GetFullPath(Path.Combine(tmp, name));
                if (!dest.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Invalid entry in embedded assets: {entry.FullName}");
                if (name.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        File.WriteAllText(Path.Combine(tmp, ".complete"), DateTimeOffset.Now.ToString("O"));
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(tmp, target);
        }
        catch (IOException) when (File.Exists(marker)) { Directory.Delete(tmp, true); } // parallel entpackt
        log.LogInformation("Web assets extracted to {Dir}", target);

        foreach (var old in Directory.GetDirectories(paths.AppCacheDir).Where(d => !d.StartsWith(target, StringComparison.OrdinalIgnoreCase)))
            try { Directory.Delete(old, true); } catch { /* alte Version evtl. noch in Benutzung */ }
        return target;
    }
}
