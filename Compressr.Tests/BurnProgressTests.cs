using Compressr.Core;

internal static class BurnProgressTests
{
    public static void Run(Action<bool, string> check)
    {
        var writing = new BurnProgressSnapshot(4, 100, 1000, 599, 60, 90, 150);
        var update = BurnProgress.Describe(writing);
        check(update.Fraction == .5 && update.Stage.Contains("writing") &&
            update.Detail.Contains("1:30 remaining") && update.Detail.Contains("excludes app read-back"),
            "burn percentage uses last written sector (inclusive), not elapsed time; ETA comes from writer");
        check(BurnProgress.Describe(writing with { LastWrittenLba = 99 }).Fraction == 0 &&
            BurnProgress.Describe(writing with { LastWrittenLba = 1099 }).Fraction == 1, "burn sector bounds cover zero and complete data");
        check(BurnProgress.Describe(writing with { LastWrittenLba = long.MaxValue }).Fraction == 1 &&
            BurnProgress.Describe(writing with { LastWrittenLba = long.MinValue }).Fraction == 0, "burn percentage clamps out-of-range writer counters without overflow");
        check(BurnProgress.Describe(writing with { SectorCount = 0 }).Fraction == null, "unknown sector count has no fake percentage");
        update = BurnProgress.Describe(writing with { RemainingSeconds = 0, TotalSeconds = 0 });
        check(update.Fraction == .5 && update.Detail.Contains("not available yet") && !update.Detail.Contains("remaining ("), "unknown writer ETA is explicit");
        check(BurnProgress.Describe(writing with { RemainingSeconds = 120 }).Detail.Contains("2:00 remaining"),
            "writer ETA can increase without synthetic smoothing");
        foreach (int action in new[] { 0, 1, 2, 3, 5, 6, 7, 99 })
            check(BurnProgress.Describe(writing with { Action = action }).Fraction == null,
                "non-data burn stage never invents sector/timer progress " + action);
        update = BurnProgress.Describe(writing with { Action = 6 });
        check(update.Detail.Contains("not yet verified") && update.Detail.Contains("SHA-256"),
            "IMAPI completed event is not successful app read-back");
    }
}
