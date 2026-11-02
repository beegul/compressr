using Compressr.Core;
using System.ComponentModel;

internal static class DiscPreparationTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var blank = new DvdMedia(7, true, true, true, true, 2295104, 2295104);
        var recorded = new DiscInspection(blank with
        {
            PhysicallyBlank = false, HeuristicallyBlank = false, FreeSectors = 0, CurrentStatus = 0x5000
        }, true, "writer:media-change-1");

        async Task RejectAsync<T>(FakeDisc fake, string name, CancellationToken token = default) where T : Exception
        {
            try { await fake.RunAsync(token); }
            catch (T) { check(fake.Burns == 0, name + ": no burn"); return; }
            throw new Exception("FAIL: expected " + typeof(T).Name + ": " + name);
        }

        foreach (int type in new[] { 6, 7, 8, 9, 10, 11, 13 })
        {
            var fake = new FakeDisc(new(blank with { Type = type }, false, null));
            check(await fake.RunAsync() == DiscPreparationResult.Ready, "blank DVD preflight passes " + type);
            check(fake.Erases == 0 && fake.ErasePrompts == 0 && fake.Burns == 1 && fake.BurnPrompts == 1,
                "blank disc never erased or prompted for formatting " + type);
        }
        foreach (int type in new[] { 6, 8, 9, 11 })
        {
            var fake = new FakeDisc(recorded with { Media = recorded.Media with { Type = type } });
            await RejectAsync<InvalidOperationException>(fake, "nonblank write-once " + type);
            check(fake.ErasePrompts == 0 && fake.Erases == 0, "write-once cannot offer destructive preparation");
        }
        foreach (int type in new[] { 7, 10, 13 })
        foreach (var blankness in new[] { (Physical: true, Logical: false), (Physical: false, Logical: true), (Physical: true, Logical: true) })
        {
            var fake = new FakeDisc(new(blank with
            {
                Type = type, PhysicallyBlank = blankness.Physical, HeuristicallyBlank = blankness.Logical,
                CurrentStatus = 3
            }, false, null));
            check(await fake.RunAsync() == DiscPreparationResult.Ready && fake.Erases == 0 &&
                fake.ErasePrompts == 0 && fake.Burns == 1, "writable physically/logically blank RW skips erase even without erase support " + type + " " + blankness);
        }
        foreach (int status in new[] { 0, 0x400, 0x2000, 0x8000 })
        {
            var fake = new FakeDisc(new(blank with { CurrentStatus = status }, true, "writer:blank"));
            await RejectAsync<InvalidOperationException>(fake, "blank flags cannot hide unknown/damaged/protected media");
            check(fake.Erases == 0 && fake.ErasePrompts == 0, "blocking state never causes automatic erasure");
        }
        foreach (int type in new[] { 7, 10, 13 })
        {
            var fake = new FakeDisc(recorded with { Media = recorded.Media with { Type = type } });
            check(await fake.RunAsync() == DiscPreparationResult.ErasedAndReady, "supported RW rechecked after preparation " + type);
            check(fake.Erases == 1 && fake.Inspections == 3 && fake.ErasePrompts == 1 && fake.BurnPrompts == 1 && fake.Burns == 1,
                "RW uses separate destructive and burn permissions " + type);
        }
        var requiresErase = new FakeDisc(new(blank with { CurrentStatus = 0x800 }, true, "writer:1"));
        check(await requiresErase.RunAsync() == DiscPreparationResult.ErasedAndReady && requiresErase.ErasePrompts == 1,
            "nominal blank RW with erase-required status is explicitly prepared, not silently written");
        var declined = new FakeDisc(recorded) { ErasePermission = false };
        check(await declined.RunAsync() == DiscPreparationResult.Declined && declined.Erases == 0 &&
            declined.Burns == 0 && declined.BurnPrompts == 0, "declining erasure leaves disc alone and never starts burn");
        var declinedBurn = new FakeDisc(recorded) { BurnPermission = false };
        check(await declinedBurn.RunAsync() == DiscPreparationResult.ErasedAndReady && declinedBurn.Erases == 1 &&
            declinedBurn.Burns == 0, "erase permission is not burn permission");
        using (var cancellation = new CancellationTokenSource())
        {
            var fake = new FakeDisc(recorded) { OnErasePrompt = cancellation.Cancel };
            await RejectAsync<OperationCanceledException>(fake, "cancel after permission before erasure", cancellation.Token);
            check(fake.Erases == 0, "cancel before API leaves medium unchanged");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var fake = new FakeDisc(recorded);
            await RejectAsync<OperationCanceledException>(fake, "cancel before inspection", cancellation.Token);
            check(fake.Inspections == 0 && fake.Erases == 0 && fake.ErasePrompts == 0, "pre-cancel does nothing");
        }
        var oversized = new FakeDisc(recorded) { RequiredBytes = blank.AvailableBytes + 2048 };
        await RejectAsync<InvalidOperationException>(oversized, "image too large before erasure");
        check(oversized.ErasePrompts == 0 && oversized.Erases == 0, "capacity failure occurs before destructive question");
        foreach (var invalid in new[]
        {
            recorded with { EraseSupported = false, EraseUnavailable = "unsupported erase API" },
            recorded with { MediaIdentity = null },
            recorded with { Media = recorded.Media with { Type = 4 } },
            recorded with { Media = recorded.Media with { Type = 3 } },
            recorded with { Media = recorded.Media with { Type = 5 } },
            recorded with { Media = recorded.Media with { RecorderSupported = false } },
            recorded with { Media = recorded.Media with { TotalSectors = 0 } },
            recorded with { Media = recorded.Media with { CurrentStatus = 0x2000 } },
            recorded with { Media = recorded.Media with { CurrentStatus = 0x400 } },
            recorded with { Media = recorded.Media with { CurrentStatus = 0x8000 } },
            recorded with { Media = recorded.Media with { CurrentStatus = 0 } }
        })
        {
            var fake = new FakeDisc(invalid);
            await RejectAsync<InvalidOperationException>(fake, "unsupported or unsafe preparation");
            check(fake.ErasePrompts == 0 && fake.Erases == 0, "unsafe media never offered erasure");
        }
        foreach (var changed in new[]
        {
            recorded with { MediaIdentity = "writer:media-change-2" },
            recorded with { Media = recorded.Media with { Type = 10 } },
            recorded with { Media = recorded.Media with { TotalSectors = 100 } },
            recorded with { Media = recorded.Media with { Type = 0 } },
            new DiscInspection(blank, true, recorded.MediaIdentity),
            recorded with { EraseSupported = false }
        })
        {
            var fake = new FakeDisc(recorded) { Replacement = changed };
            await RejectAsync<InvalidOperationException>(fake, "changed medium after permission");
            check(fake.Erases == 0, "changed disc never erased, including identical media with new change counter");
        }
        var eraseFailure = new FakeDisc(recorded) { EraseFailure = new IOException("IMAPI erase failure") };
        await RejectAsync<IOException>(eraseFailure, "erase API failure");
        check(eraseFailure.Erases == 1 && eraseFailure.BurnPrompts == 0, "failed erase never continues");
        var stillRecorded = new FakeDisc(recorded) { PostErase = recorded };
        await RejectAsync<InvalidOperationException>(stillRecorded, "erase reported success but medium not blank");
        var shrunk = new FakeDisc(recorded)
        {
            RequiredBytes = 4096,
            PostErase = new(blank with { TotalSectors = 1, FreeSectors = 1 }, true, "writer:2")
        };
        await RejectAsync<InvalidOperationException>(shrunk, "post-erase capacity changed");
        var busy = new FakeDisc(recorded) { InspectFailure = new IOException("Writer busy or locked") };
        await RejectAsync<IOException>(busy, "busy or inaccessible writer");
        check(busy.Erases == 0 && busy.ErasePrompts == 0, "busy writer cannot erase");
        var checkFailure = new FakeDisc(recorded) { PostInspectionFailure = new IOException("Media removed after erase") };
        await RejectAsync<IOException>(checkFailure, "postcheck failure is not readiness success");

        var automatic = new DvdOutputSelection();
        var untouched = new FakeDisc(recorded);
        automatic.Apply(DiscReadiness.Assess(recorded, 2048));
        check(automatic.Selected == OutputKind.Burn && untouched.Erases == 0 && untouched.ErasePrompts == 0 && untouched.Burns == 0,
            "automatic offers Make DVD for supported RW but discovery never prompts, erases or burns");
        automatic.Choose(OutputKind.Iso);
        automatic.Apply(DiscReadiness.Assess(recorded, 2048));
        check(automatic.Selected == OutputKind.Iso && untouched.Erases == 0, "explicit ISO bypasses disc preparation even with supported RW");
        using (var cancellation = new CancellationTokenSource())
        {
            var fake = new FakeDisc(recorded) { OnErase = cancellation.Cancel };
            check(await fake.RunAsync(cancellation.Token) == DiscPreparationResult.ErasedAndReady &&
                fake.Inspections == 3, "noncancelable IMAPI stage is always postchecked even if cancellation arrives");
        }
        var preparation = new DiscPreparation();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = preparation.RunAsync(2048, async _ => { entered.SetResult(); await release.Task; return new(blank, false, null); },
            _ => throw new Exception("Unexpected confirmation"), (_, _) => throw new Exception("Unexpected erase"), CancellationToken.None);
        await entered.Task;
        try
        {
            await preparation.RunAsync(2048, _ => Task.FromResult(recorded), _ => true, (_, _) => Task.CompletedTask, CancellationToken.None);
            throw new Exception("FAIL: concurrent preparation accepted");
        }
        catch (InvalidOperationException) { check(true, "concurrent preparation rejected without API call"); }
        release.SetResult();
        check(await first == DiscPreparationResult.Ready, "first operation completes after busy rejection");

        var guard = new GuardedErase(recorded);
        guard.Exclusive = true;
        try { guard.Inspect(); throw new Exception("FAIL: fixture must reproduce locked counter access"); }
        catch (Win32Exception ex) { check(ex.NativeErrorCode == 5, "old counter-after-IMAPI-lock sequence reproduces access denied"); }
        guard.Exclusive = false;
        guard.Run();
        check(guard.Erases == 1 && string.Join(",", guard.Events) ==
            "prevent,counter,exclusive,locked-media,erase,release,allow",
            "confirmed erase locks removal, checks exact counter before IMAPI lock and rechecks media under lock");
        check(!guard.Exclusive && !guard.RemovalLocked, "successful erasure releases exclusive and caller-scoped tray locks");
        foreach (string failure in new[] { "prevent", "counter", "exclusive", "locked-media", "erase" })
        {
            guard = new(recorded) { FailAt = failure };
            try { guard.Run(); throw new Exception("FAIL: guard failure not surfaced"); }
            catch (IOException) { check(true, "guard propagates " + failure + " failure"); }
            check(!guard.Exclusive && !guard.RemovalLocked && guard.Erases == 0,
                failure + " failure releases any locks and never erases");
        }
        guard = new(recorded) { Replacement = recorded with { MediaIdentity = "writer:replacement" } };
        try { guard.Run(); throw new Exception("FAIL: replaced medium accepted"); }
        catch (InvalidOperationException) { check(guard.Erases == 0 && !guard.RemovalLocked, "same-state medium swap detected by counter before exclusive access"); }
        guard = new(recorded) { LockedMedia = recorded.Media with { TotalSectors = 1, FreeSectors = 0 } };
        try { guard.Run(); throw new Exception("FAIL: locked snapshot change accepted"); }
        catch (InvalidOperationException) { check(guard.Erases == 0 && !guard.Exclusive && !guard.RemovalLocked, "locked media state/capacity changes abort with cleanup"); }
        using (var cancellation = new CancellationTokenSource())
        {
            guard = new(recorded) { OnLockedRead = cancellation.Cancel };
            try { guard.Run(cancellation.Token); throw new Exception("FAIL: guard cancellation ignored"); }
            catch (OperationCanceledException) { check(guard.Erases == 0 && !guard.Exclusive && !guard.RemovalLocked, "cancel after acquiring locks does not erase and releases both locks"); }
        }
    }

    private sealed class GuardedErase(DiscInspection approved)
    {
        public readonly List<string> Events = [];
        public bool RemovalLocked, Exclusive;
        public int Erases;
        public string? FailAt;
        public DiscInspection? Replacement;
        public DvdMedia? LockedMedia;
        public Action? OnLockedRead;
        private void Step(string name)
        {
            Events.Add(name);
            if (FailAt == name) throw new IOException("Fixture failure: " + name);
        }
        public DiscInspection Inspect()
        {
            if (Exclusive) throw new Win32Exception(5, "Cannot inspect DVD media changes.");
            if (!RemovalLocked) throw new Exception("Counter inspection must be protected against removal.");
            Step("counter");
            return Replacement ?? approved;
        }
        public void Run(CancellationToken token = default) =>
            DiscPreparation.EraseConfirmed(approved, 2048,
                () => { Step("prevent"); RemovalLocked = true; return new Cleanup(() => { Step("allow"); RemovalLocked = false; }); },
                Inspect,
                () => { Step("exclusive"); Exclusive = true; return new Cleanup(() => { Step("release"); Exclusive = false; }); },
                () =>
                {
                    if (!Exclusive || !RemovalLocked) throw new Exception("Locked media read must hold both locks.");
                    Step("locked-media"); OnLockedRead?.Invoke(); return LockedMedia ?? approved.Media;
                },
                () => { Step("erase"); Erases++; }, token);
    }
    private sealed class Cleanup(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed class FakeDisc(DiscInspection initial)
    {
        public int Inspections, Erases, ErasePrompts, BurnPrompts, Burns;
        public bool ErasePermission = true, BurnPermission = true;
        public long RequiredBytes = 2048;
        public Action? OnErasePrompt, OnErase;
        public Exception? InspectFailure, EraseFailure, PostInspectionFailure;
        public DiscInspection? Replacement, PostErase;
        private readonly DiscPreparation preparation = new();

        public async Task<DiscPreparationResult> RunAsync(CancellationToken token = default)
        {
            var result = await preparation.RunAsync(RequiredBytes, ct =>
            {
                ct.ThrowIfCancellationRequested();
                Inspections++;
                if (InspectFailure != null) throw InspectFailure;
                if (Erases > 0 && PostInspectionFailure != null) throw PostInspectionFailure;
                return Task.FromResult(Erases > 0 ? PostErase ?? initial with
                {
                    Media = initial.Media with { PhysicallyBlank = true, HeuristicallyBlank = true,
                        FreeSectors = initial.Media.TotalSectors, CurrentStatus = 2, MediaSupported = true }
                } : Inspections > 1 ? Replacement ?? initial : initial);
            }, _ => { ErasePrompts++; OnErasePrompt?.Invoke(); return ErasePermission; }, (approved, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                approved.CheckUnchanged(Replacement ?? initial, RequiredBytes);
                Erases++;
                if (EraseFailure != null) throw EraseFailure;
                OnErase?.Invoke();
                return Task.CompletedTask;
            }, token);
            if (result != DiscPreparationResult.Declined)
            {
                BurnPrompts++;
                if (BurnPermission && !token.IsCancellationRequested) Burns++;
            }
            return result;
        }
    }
}
