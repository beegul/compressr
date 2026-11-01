namespace Compressr.Core;

public static class SourceDrop
{
    public static IReadOnlyList<string> ParseLinks(string text)
    {
        var links = text.Trim('\0', '\uFEFF', ' ', '\r', '\n', '\t')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#')).ToArray();
        if (links.Length == 0) throw new ArgumentException("Drop video files or HTTPS YouTube video links.");
        foreach (string link in links) MediaTools.ValidateYouTube(link);
        return links;
    }
}
