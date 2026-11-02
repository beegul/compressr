using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Compressr.Core;

namespace Compressr.App;

internal sealed class ImapiBurnProgress : IDisposable
{
    private static readonly Guid EventsId = new("2735413C-7F64-5B0F-8F00-5D77AFBE261E");
    private const int UpdateId = 0x200;
    private readonly object format;
    private readonly IProgress<StageProgress> progress;
    private readonly Action<object, object> update;

    public ImapiBurnProgress(object format, IProgress<StageProgress> progress)
    {
        this.format = format;
        this.progress = progress;
        update = Update;
        ComEventsHelper.Combine(format, EventsId, UpdateId, update);
    }

    public void Update(object sender, object arguments)
    {
        try
        {
            dynamic value = arguments;
            var snapshot = new BurnProgressSnapshot((int)value.CurrentAction, (long)value.StartLba,
                (long)value.SectorCount, (long)value.LastWrittenLba, (int)value.ElapsedTime,
                (int)value.RemainingTime, (int)value.TotalTime);
            progress.Report(BurnProgress.Describe(snapshot));
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException or OverflowException)
        {
            System.Diagnostics.Trace.TraceWarning($"IMAPI burn progress could not be read: {ex.Message}");
            progress.Report(new("Burn", null, "The writer's progress update is unavailable. Writing may still be in progress; wait for finalisation and read-back.\n" + ex.Message));
        }
    }

    public void Dispose() => ComEventsHelper.Remove(format, EventsId, UpdateId, update);
}
