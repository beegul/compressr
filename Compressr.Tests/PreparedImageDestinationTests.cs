using Compressr.Core;

internal static class PreparedImageDestinationTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "compressr-destination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "retryable.iso");
            string partial = Path.Combine(root, "retryable.iso.partial");
            byte[] firstImage = new byte[2048];
            firstImage[0] = 1;
            File.WriteAllBytes(partial, firstImage);
            var fresh = PreparedImageDestination.Inspect(destination, 1024);
            check(!fresh.Existed && fresh.Path == destination, "custom absolute ISO destination passes capacity and folder preflight");
            PreparedImageDestination.Commit(partial, fresh);
            check(File.ReadAllBytes(destination).SequenceEqual(firstImage) && !File.Exists(partial),
                "sector-aligned staged image is atomically committed without changing its bytes");

            var existing = PreparedImageDestination.Inspect(destination, 0);
            byte[] secondImage = new byte[2048];
            secondImage[0] = 5;
            File.WriteAllBytes(partial, secondImage);
            PreparedImageDestination.Commit(partial, existing);
            check(File.ReadAllBytes(destination).SequenceEqual(secondImage),
                "existing ISO replacement is allowed only against the inspected unchanged destination");

            string conflict = Path.Combine(root, "new.iso");
            string conflictPartial = conflict + ".partial";
            var absent = PreparedImageDestination.Inspect(conflict, 0);
            File.WriteAllBytes(conflict, [8]);
            byte[] thirdImage = new byte[2048];
            thirdImage[0] = 9;
            File.WriteAllBytes(conflictPartial, thirdImage);
            try { PreparedImageDestination.Commit(conflictPartial, absent); check(false, "new destination race must fail"); }
            catch (IOException) { check(File.ReadAllBytes(conflict).SequenceEqual(new byte[] { 8 }) && File.Exists(conflictPartial),
                "newly appeared destination is preserved and verified partial is not discarded"); }

            var changed = PreparedImageDestination.Inspect(destination, 0);
            File.WriteAllBytes(destination, [10, 11]);
            byte[] fourthImage = new byte[2048];
            fourthImage[0] = 12;
            File.WriteAllBytes(partial, fourthImage);
            try { PreparedImageDestination.Commit(partial, changed); check(false, "changed existing destination must fail"); }
            catch (IOException) { check(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 10, 11 }),
                "destination changed while preparing is never silently overwritten"); }

            Throws<ArgumentException>(() => PreparedImageDestination.Inspect(Path.Combine(root, "dvd-folder"), 0),
                "DVD image destination requires ISO extension");
            Throws<DirectoryNotFoundException>(() => PreparedImageDestination.Inspect(Path.Combine(root, "missing", "dvd.iso"), 0),
                "missing destination folder fails clearly");
            string folderTarget = Path.Combine(root, "folder.iso");
            Directory.CreateDirectory(folderTarget);
            Throws<IOException>(() => PreparedImageDestination.Inspect(folderTarget, 0), "destination cannot be a directory");
            Throws<IOException>(() => PreparedImageDestination.Inspect(Path.Combine(root, "low-space.iso"),
                long.MaxValue - PreparedImageDestination.SafetyReserveBytes),
                "insufficient disk space is checked before work");
            Throws<InvalidDataException>(() => PreparedImageDestination.Commit(Path.Combine(root, "empty.partial"),
                new(Path.Combine(root, "empty.iso"), false, null, null)), "empty image cannot be committed");
            CheckTemporaryProbeFilesWereNotCreated(root, check);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckTemporaryProbeFilesWereNotCreated(string root, Action<bool, string> check) =>
        check(!Directory.EnumerateFiles(root, ".compressr-*").Any(), "destination validation leaves no temporary probe files");

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("FAIL: expected " + typeof(T).Name + ": " + message);
    }
}
