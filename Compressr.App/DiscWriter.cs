using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
using Compressr.Core;

namespace Compressr.App;

public sealed record DvdDrive(string Id, string Label, string Mount);

public static class DiscWriter
{
    private static dynamic Com(string name) => Activator.CreateInstance(Type.GetTypeFromProgID(name, true)!)!;
    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    public static IReadOnlyList<DvdDrive> GetDrives()
    {
        var drives = new List<DvdDrive>();
        object? masterObject = null;
        try
        {
            dynamic master = masterObject = Com("IMAPI2.MsftDiscMaster2");
            if (!(bool)master.IsSupportedEnvironment) return drives;
            for (int i = 0; i < (int)master.Count; i++)
            {
                object? recorderObject = null;
                try
                {
                    string id = master[i];
                    dynamic recorder = recorderObject = Com("IMAPI2.MsftDiscRecorder2");
                    recorder.InitializeDiscRecorder(id);
                    var mounts = ((Array)recorder.VolumePathNames).Cast<string>().ToArray();
                    drives.Add(new(id, $"{recorder.VendorId} {recorder.ProductId} ({string.Join(", ", mounts)})",
                        mounts.FirstOrDefault() ?? ""));
                }
                finally { Release(recorderObject); }
            }
            return drives;
        }
        finally { Release(masterObject); }
    }

    public static void CheckBlank(DvdDrive drive, long requiredBytes)
        => Inspect(drive).Check(requiredBytes);

    public static DvdMedia Inspect(DvdDrive drive)
    {
        object? recorderObject = null, formatObject = null;
        try
        {
            dynamic recorder = recorderObject = Com("IMAPI2.MsftDiscRecorder2");
            recorder.InitializeDiscRecorder(drive.Id);
            CheckAvailable(recorder);
            dynamic format = formatObject = Com("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            return ReadMedia(recorder, format);
        }
        finally { Release(formatObject); Release(recorderObject); }
    }

    private static DvdMedia ReadMedia(dynamic recorder, dynamic format)
    {
        bool recorderSupported = (bool)format.IsRecorderSupported(recorder);
        bool mediaSupported = recorderSupported && (bool)format.IsCurrentMediaSupported(recorder);
        return new((int)format.CurrentPhysicalMediaType, recorderSupported, mediaSupported,
            (bool)format.MediaPhysicallyBlank, (bool)format.MediaHeuristicallyBlank,
            (long)format.TotalSectorsOnMedia, (long)format.FreeSectorsOnMedia, (int)format.CurrentMediaStatus);
    }

    private static void CheckAvailable(dynamic recorder)
    {
        string? owner = recorder.ExclusiveAccessOwner as string;
        if (!string.IsNullOrEmpty(owner))
            throw new InvalidOperationException($"The DVD writer is busy or locked by {owner}. Close that operation and check disc again.");
    }

    public static DiscInspection InspectPreparation(DvdDrive drive)
    {
        object? recorderObject = null, dataObject = null, eraseObject = null;
        try
        {
            dynamic recorder = recorderObject = Com("IMAPI2.MsftDiscRecorder2");
            recorder.InitializeDiscRecorder(drive.Id);
            CheckAvailable(recorder);
            dynamic data = dataObject = Com("IMAPI2.MsftDiscFormat2Data");
            data.Recorder = recorder;
            var media = ReadMedia(recorder, data);
            if (media.Type is not (7 or 10 or 13)) return new(media, false, null);
            try
            {
                string identity = MediaIdentity(drive);
                dynamic erase = eraseObject = Com("IMAPI2.MsftDiscFormat2Erase");
                erase.Recorder = recorder;
                bool supported = (bool)erase.IsRecorderSupported(recorder) && (bool)erase.IsCurrentMediaSupported(recorder);
                var current = ReadMedia(recorder, data);
                if (identity != MediaIdentity(drive) || media != current)
                    throw new IOException("The disc changed during inspection. Check disc again.");
                return new(media, supported, identity, supported ? null : "IMAPI does not support erasing this recorder/media combination");
            }
            catch (Exception ex) when (ex is COMException or IOException or Win32Exception or InvalidOperationException)
            { return new(media, false, null, ex.Message); }
        }
        finally { Release(eraseObject); Release(dataObject); Release(recorderObject); }
    }

    public static void Erase(DvdDrive drive, DiscInspection approved, long requiredBytes, CancellationToken token)
    {
        object? recorderObject = null, dataObject = null, eraseObject = null;
        bool locked = false;
        try
        {
            token.ThrowIfCancellationRequested();
            dynamic recorder = recorderObject = Com("IMAPI2.MsftDiscRecorder2");
            recorder.InitializeDiscRecorder(drive.Id);
            CheckAvailable(recorder);
            recorder.AcquireExclusiveAccess(true, "Compressr");
            locked = true;
            dynamic data = dataObject = Com("IMAPI2.MsftDiscFormat2Data");
            data.Recorder = recorder;
            dynamic erase = eraseObject = Com("IMAPI2.MsftDiscFormat2Erase");
            erase.Recorder = recorder;
            erase.ClientName = "Compressr";
            erase.FullErase = false;
            bool supported = (bool)erase.IsRecorderSupported(recorder) && (bool)erase.IsCurrentMediaSupported(recorder);
            var current = new DiscInspection(ReadMedia(recorder, data), supported, MediaIdentity(drive));
            approved.CheckUnchanged(current, requiredBytes);
            token.ThrowIfCancellationRequested();
            // IMAPI chooses the appropriate DVD+RW overwrite / DVD-RW erase operation.
            // EraseMedia is synchronous and exposes no cancellation API.
            erase.EraseMedia();
        }
        finally
        {
            try { if (locked) ((dynamic)recorderObject!).ReleaseExclusiveAccess(); }
            finally { Release(eraseObject); Release(dataObject); Release(recorderObject); }
        }
    }

    private static string MediaIdentity(DvdDrive drive)
    {
        string root = Path.GetPathRoot(drive.Mount) ?? "";
        if (root.Length < 2 || root[1] != ':')
            throw new IOException("Media-change detection requires a drive-letter mount. Use another writer or a new blank DVD.");
        using var handle = CreateFile(@"\\.\" + root[..2], 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect DVD media changes.");
        if (!DeviceIoControl(handle, 0x2D4800, IntPtr.Zero, 0, out uint changes, 4, out uint bytes, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect DVD media changes.");
        if (bytes != 4) throw new IOException("The writer did not supply a media-change counter. Erasure is unavailable.");
        return $"{drive.Id}:{changes}";
    }

    public static void Burn(PreparedDvd dvd, DvdDrive drive, IProgress<StageProgress> progress)
    {
        object? recorderObject = null, formatObject = null;
        IStream? stream = null;
        try
        {
            dynamic recorder = recorderObject = Com("IMAPI2.MsftDiscRecorder2");
            recorder.InitializeDiscRecorder(drive.Id);
            dynamic format = formatObject = Com("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            format.ClientName = "Compressr";
            format.ForceMediaToBeClosed = true;
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(dvd.Path, 0x20, 0, false, null, out stream));
            ReadMedia(recorder, format).Check(dvd.Length);
            progress.Report(new("Burn", null, "Writing and finalising the disc. Do not remove it. This stage cannot be cancelled."));
            format.Write(stream);
        }
        finally { Release(stream); Release(formatObject); Release(recorderObject); }
    }

    public static async Task VerifyAsync(PreparedDvd dvd, DvdDrive drive, IProgress<StageProgress> progress)
    {
        progress.Report(new("Verify", null, "Waiting for the finalised disc to mount…"));
        string target = Path.Combine(drive.Mount, "VIDEO_TS");
        for (int i = 0; i < 30 && !Directory.Exists(target); i++) await Task.Delay(1000);
        if (drive.Mount.Length == 0 || !Directory.Exists(target))
            throw new IOException("Burn completed, but Windows did not mount the disc for verification. The disc is not verified.");
        await dvd.VerifyDirectoryAsync(target, progress, CancellationToken.None);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IStream? template, out IStream stream);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
        out uint output, uint outputSize, out uint returned, IntPtr overlapped);
}
