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
            throw new InvalidOperationException($"The DVD writer is busy or locked by {owner}. Close that operation and try Make DVD again.");
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
                    throw new IOException("The disc changed during inspection. Try Make DVD again.");
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
        try
        {
            token.ThrowIfCancellationRequested();
            dynamic recorder = recorderObject = Com("IMAPI2.MsftDiscRecorder2");
            recorder.InitializeDiscRecorder(drive.Id);
            CheckAvailable(recorder);
            dynamic data = dataObject = Com("IMAPI2.MsftDiscFormat2Data");
            data.Recorder = recorder;
            dynamic erase = eraseObject = Com("IMAPI2.MsftDiscFormat2Erase");
            erase.Recorder = recorder;
            erase.ClientName = "Compressr";
            erase.FullErase = false;
            DiscPreparation.EraseConfirmed(approved, requiredBytes,
                () => new MediaRemovalLock(drive),
                () => new DiscInspection(ReadMedia(recorder, data),
                    (bool)erase.IsRecorderSupported(recorder) && (bool)erase.IsCurrentMediaSupported(recorder), MediaIdentity(drive)),
                () =>
                {
                    recorder.AcquireExclusiveAccess(true, "Compressr");
                    return new ExclusiveLock(recorderObject!);
                },
                () => ReadMedia(recorder, data),
                () => erase.EraseMedia(), token);
        }
        finally
        {
            Release(eraseObject); Release(dataObject); Release(recorderObject);
        }
    }

    private static string DevicePath(DvdDrive drive)
    {
        string root = Path.GetPathRoot(drive.Mount) ?? "";
        if (root.Length < 2 || root[1] != ':')
            throw new IOException("Media-change detection requires a drive-letter mount. Use another writer or a new blank DVD.");
        return @"\\.\" + root[..2];
    }
    private static Win32Exception DeviceError(DvdDrive drive, string action)
    {
        int error = Marshal.GetLastWin32Error();
        return new(error, $"{action} ({drive.Label}): {new Win32Exception(error).Message} (Windows error {error}). Try Make DVD again.");
    }
    private static string MediaIdentity(DvdDrive drive)
    {
        using var handle = CreateFile(DevicePath(drive), 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw DeviceError(drive, "Cannot open the DVD for media-change inspection");
        if (!DeviceIoControl(handle, 0x2D4800, IntPtr.Zero, 0, out uint changes, 4, out uint bytes, IntPtr.Zero))
            throw DeviceError(drive, "Cannot inspect DVD media changes");
        if (bytes != 4) throw new IOException("The writer did not supply a media-change counter. Erasure is unavailable.");
        return $"{drive.Id}:{changes}";
    }

    private sealed class ExclusiveLock(object recorder) : IDisposable
    {
        public void Dispose() => ((dynamic)recorder).ReleaseExclusiveAccess();
    }

    private sealed class MediaRemovalLock : IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly DvdDrive drive;
        public MediaRemovalLock(DvdDrive drive)
        {
            this.drive = drive;
            handle = CreateFile(DevicePath(drive), 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = DeviceError(drive, "Cannot lock DVD removal for safe preparation");
                handle.Dispose();
                throw error;
            }
            if (!SetRemoval(1))
            {
                var error = DeviceError(drive, "Cannot prevent DVD removal for safe preparation");
                handle.Dispose();
                throw error;
            }
        }
        private bool SetRemoval(byte prevent) =>
            // Caller-scoped IOCTL_STORAGE_EJECTION_CONTROL also releases on process exit.
            DeviceIoControlRemoval(handle, 0x2D0940, [prevent], 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        public void Dispose()
        {
            try
            {
                if (!SetRemoval(0)) throw DeviceError(drive, "Cannot release the DVD removal lock");
            }
            finally { handle.Dispose(); }
        }
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
            try { format.Write(stream); }
            catch (Exception ex) when (ex is COMException or IOException)
            {
                throw new IOException($"Writing / finalising DVD failed in {drive.Label}: {ex.Message} (HRESULT 0x{ex.HResult:X8}).\n" +
                    $"The disc is NOT verified. Keep the prepared ISO at {dvd.Path}. Check the disc, drive and connection; do not erase or retry automatically.", ex);
            }
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
        try { await dvd.VerifyDirectoryAsync(target, progress, CancellationToken.None); }
        catch (DvdReadbackException ex)
        {
            throw new IOException($"Read-back from {drive.Label} failed after the write/finalise call returned.\n{ex.Message}\n" +
                $"Prepared ISO retained at {dvd.Path}. A successful write return alone does not prove a readable DVD.", ex);
        }
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

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlRemoval(SafeFileHandle device, uint code, byte[] input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
