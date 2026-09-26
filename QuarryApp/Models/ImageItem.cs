namespace QuarryApp.Models;

public class ImageItem
{
    public string Key { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? FilenameHint { get; set; }
    public string? Referer { get; set; }
    public long? Bytes { get; set; }
}
