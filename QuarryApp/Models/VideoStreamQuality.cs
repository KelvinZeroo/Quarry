namespace QuarryApp.Models;

public class VideoStreamQuality
{
    public string QualityLabel { get; set; } = "Default / Best";
    public string DirectUrl { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public int TargetHeight { get; set; }
    public string Extension { get; set; } = ".mp4";
    public long? ContentLength { get; set; }
    public bool IsAudioOnly { get; set; }
    public bool IsMuxed { get; set; }

    public string DisplayText
    {
        get
        {
            var sizeStr = ContentLength > 0 ? $" ({ContentLength.Value / (1024.0 * 1024.0):F1} MB)" : "";
            if (IsAudioOnly) return $"{QualityLabel}{sizeStr}";
            return $"{QualityLabel} [{Resolution}]{sizeStr}";
        }
    }
}
