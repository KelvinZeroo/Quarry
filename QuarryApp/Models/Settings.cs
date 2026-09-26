using System.IO;

namespace QuarryApp.Models;

public class Settings
{
    public string OutDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "Quarry"
    );
    public int Workers { get; set; } = 6;
    public int TimeoutSeconds { get; set; } = 30;
    public double DelaySeconds { get; set; } = 0.05;
    public int Retries { get; set; } = 3;
    public bool Force { get; set; } = false;
    public int? MaxImages { get; set; }
    public bool Scan { get; set; } = false;
    public bool AutoScan { get; set; } = true;
    public bool AskHowMany { get; set; } = true;
    public int MaxScan { get; set; } = 2000;
    public int AskAbove { get; set; } = 500;
    public int Port { get; set; } = 8765;
    public bool CatchClipboard { get; set; } = true;
    public string FontFamily { get; set; } = "Manrope";
    public double FontSize { get; set; } = 12.0;
    public bool EnableSpeedLimit { get; set; } = false;
    public long SpeedLimitKbps { get; set; } = 0; // 0 = unlimited, e.g. 5120 for 5 MB/s
    public bool AutoExtractArchives { get; set; } = true;
    public bool SchedulerEnabled { get; set; } = false;
    public string SchedulerStartTime { get; set; } = "01:00";
    public string SchedulerStopTime { get; set; } = "07:00";

    // Torrent & P2P Network Settings
    public int TorrentPort { get; set; } = 6881;
    public bool EnableDHT { get; set; } = true;
    public bool EnableUPnP { get; set; } = true;
    public bool EnablePeX { get; set; } = true;
    public int MaxTorrentPeers { get; set; } = 50;
    public string DefaultTrackers { get; set; } =
        "udp://tracker.opentrackr.org:1337/announce\n" +
        "udp://open.stealth.si:80/announce\n" +
        "udp://tracker.torrent.eu.org:451/announce\n" +
        "udp://tracker.bittor.pw:1337/announce\n" +
        "udp://public.popcorn-tracker.org:6969/announce\n" +
        "udp://explodie.org:6969/announce";

    // Proxy & Interface Binding Settings
    public string ProxyType { get; set; } = "None"; // None, HTTP, SOCKS5
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUser { get; set; } = "";
    public string ProxyPass { get; set; } = "";
    public bool AssociateMagnet { get; set; } = true;
    public bool AssociateTorrent { get; set; } = true;
}
