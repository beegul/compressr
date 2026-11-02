using Compressr.Core;

internal static class DvdImageNameTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "dvd-names");
        SourceVideo Local(string name) => new(Path.Combine(folder, name));
        string Name(params SourceVideo[] sources) => Path.GetFileName(DvdImageName.Suggest(folder, sources));
        check(Name(Local("Holiday in Wales.mp4")) == "Holiday in Wales.iso", "local file stem names the ISO");
        var link = new SourceVideo("https://youtu.be/fixture");
        check(Name(link) == "YouTube video.iso", "unresolved URL is not used as a filename");
        await link.MetadataAsync((_, _) => Task.FromResult(new YouTubeInfo("Concert: Part 1 / finale?", 60)), CancellationToken.None);
        check(Name(link) == "Concert_ Part 1 _ finale_.iso", "metadata title names the ISO and invalid Windows characters are sanitized");
        check(Name(Local("First.mp4"), link, Local("Last.mov")) == "First and 2 more.iso", "multi-clip naming follows first queued clip and exact remaining count");
        check(Name(link, Local("First.mp4")) == "Concert_ Part 1 _ finale_ and 1 more.iso", "reordered sources deterministically change the suggested name");
        foreach (string reserved in new[] { "CON", "con.foo", "PRN", "AUX", "NUL", "COM1", "COM9", "LPT1", "LPT\u00b2" })
            check(Name(Local(reserved + ".mp4")).StartsWith('_'), "reserved Windows device name is escaped: " + reserved);
        check(Name(Local("...mp4")) == "DVD video.iso", "empty/dot-only stem has a usable fallback");
        var longTitle = new SourceVideo("https://youtu.be/long");
        await longTitle.MetadataAsync((_, _) => Task.FromResult(new YouTubeInfo(new string('a', 119) + "\U0001F3B5" + "end. ", 10)), CancellationToken.None);
        string result = Name(longTitle);
        check(result.Length <= 124 && !char.IsHighSurrogate(Path.GetFileNameWithoutExtension(result)[^1]), "long Unicode titles are bounded without splitting a surrogate pair");
        check(Path.GetDirectoryName(DvdImageName.Suggest(folder, [link])) == folder, "automatic naming preserves the selected folder");
        check(Name() == "Compressr.iso", "empty queue has a neutral placeholder");
    }
}
