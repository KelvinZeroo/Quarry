namespace QuarryApp.Adapters;

public static class AdapterFactory
{
    private static readonly YouTubeAdapter YouTube = new();
    private static readonly SocialMediaAdapter SocialMedia = new();
    private static readonly MatureMediaAdapter MatureMedia = new();
    private static readonly CloudHosterAdapter CloudHoster = new();
    private static readonly GenericAdapter Fallback = new();

    public static ISiteAdapter GetAdapter(string url)
    {
        if (YouTube.Matches(url)) return YouTube;
        if (SocialMedia.Matches(url)) return SocialMedia;
        if (MatureMedia.Matches(url)) return MatureMedia;
        if (CloudHoster.Matches(url)) return CloudHoster;

        var lower = url.ToLowerInvariant();
        if (lower.Contains("youtube.com") || lower.Contains("youtu.be")) return YouTube;
        if (SocialMedia.Matches(url)) return SocialMedia;
        if (MatureMedia.Matches(url)) return MatureMedia;
        if (CloudHoster.Matches(url)) return CloudHoster;

        return Fallback;
    }
}
