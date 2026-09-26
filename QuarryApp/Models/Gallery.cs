namespace QuarryApp.Models;

public class Gallery
{
    public string Site { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string GalleryId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public List<ImageItem> Items { get; set; } = new();
    public int? TotalExpected { get; set; }
    public long? TotalBytes { get; set; }
}
