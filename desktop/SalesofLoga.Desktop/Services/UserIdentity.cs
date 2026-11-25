using System.Runtime.InteropServices;
using System.Text;

namespace SalesofLoga.Desktop.Services;

public sealed record AppUser(string Id, string Name, string Account, string Upn);

/// <summary>
/// Ermittelt den angemeldeten Windows-Benutzer (keine Eingabe, nicht fälschbar über die Oberfläche).
/// Anzeigename und UPN kommen aus dem AD bzw. Entra ID (GetUserNameEx), sonst DOMAIN\user.
/// </summary>
public static class UserIdentity
{
    private const int NameDisplay = 3;
    private const int NameUserPrincipal = 8;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameExW(int nameFormat, StringBuilder? buffer, ref uint size);

    public static AppUser Current(ILogger? log = null)
    {
        var account = OperatingSystem.IsWindows()
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : Environment.UserName;
        string? display = null, upn = null;
        if (OperatingSystem.IsWindows())
        {
            display = TryGet(NameDisplay, log);
            upn = TryGet(NameUserPrincipal, log);
        }
        var name = Normalize(display) ?? Environment.UserName;
        return new AppUser($"win:{account.ToLowerInvariant()}", name, account, (upn ?? "").ToLowerInvariant());
    }

    /// <summary>"König, Daniel" → "Daniel König" (übliches AD-Format)</summary>
    private static string? Normalize(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;
        var d = display.Trim();
        var parts = d.Split(',', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 && !parts[1].Contains(',') ? $"{parts[1]} {parts[0]}" : d;
    }

    private static string? TryGet(int format, ILogger? log)
    {
        try
        {
            uint size = 0;
            GetUserNameExW(format, null, ref size);
            if (size == 0) return null;
            var sb = new StringBuilder((int)size);
            return GetUserNameExW(format, sb, ref size) ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "GetUserNameEx({Format}) failed", format);
            return null;
        }
    }
}
