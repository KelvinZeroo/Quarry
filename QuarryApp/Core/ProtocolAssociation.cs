using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace QuarryApp.Core;

/// <summary>
/// Windows Protocol & File Association Manager.
/// Registers Quarry as default handler for 'magnet:' URIs and '.torrent' file types.
/// </summary>
public static class ProtocolAssociation
{
    private const string MagnetScheme = "magnet";
    private const string TorrentExtension = ".torrent";
    private const string TorrentProgId = "Quarry.Torrent";

    public static string GetExecutablePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName ??
               Path.Combine(AppContext.BaseDirectory, "QuarryApp.exe");
    }

    public static bool IsMagnetRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command");
            if (key != null)
            {
                var val = key.GetValue("") as string;
                return !string.IsNullOrEmpty(val) && val.Contains("QuarryApp", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    public static bool IsTorrentRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{TorrentProgId}\shell\open\command");
            if (key != null)
            {
                var val = key.GetValue("") as string;
                return !string.IsNullOrEmpty(val) && val.Contains("QuarryApp", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    public static bool RegisterMagnetProtocol()
    {
        try
        {
            var exePath = GetExecutablePath();
            var command = $"\"{exePath}\" \"%1\"";

            using var magnetKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet");
            magnetKey.SetValue("", "URL:BitTorrent Magnet Link");
            magnetKey.SetValue("URL Protocol", "");

            using var cmdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet\shell\open\command");
            cmdKey.SetValue("", command);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool RegisterTorrentExtension()
    {
        try
        {
            var exePath = GetExecutablePath();
            var command = $"\"{exePath}\" \"%1\"";

            using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{TorrentExtension}");
            extKey.SetValue("", TorrentProgId);

            using var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{TorrentProgId}");
            progKey.SetValue("", "BitTorrent File");

            using var cmdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{TorrentProgId}\shell\open\command");
            cmdKey.SetValue("", command);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool UnregisterMagnetProtocol()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\magnet", throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool UnregisterTorrentExtension()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{TorrentProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{TorrentExtension}", throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
