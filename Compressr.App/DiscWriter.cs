using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
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
            dynamic format = formatObject = Com("IMAPI2.MsftDiscFormat2Data");
            format.Recorder = recorder;
            return ReadMedia(recorder, format);
        }
        finally { Release(formatObject); Release(recorderObject); }
    }

    private static DvdMedia ReadMedia(dynamic recorder, dynamic format)
    {
        bool supported = (bool)format.IsRecorderSupported(recorder) && (bool)format.IsCurrentMediaSupported(recorder);
        if (!supported) throw new InvalidOperationException("Insert a supported writable DVD in the selected drive.");
        return new((int)format.CurrentPhysicalMediaType, true, true,
            (bool)format.MediaPhysicallyBlank, (bool)format.MediaHeuristicallyBlank,
            (long)format.TotalSectorsOnMedia, (long)format.FreeSectorsOnMedia, (int)format.CurrentMediaStatus);
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
}
