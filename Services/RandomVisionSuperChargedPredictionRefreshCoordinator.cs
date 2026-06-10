using Godot;
using MegaCrit.Sts2.Core.Random;

namespace RandomVisionSuperCharged.Services;

internal static class RandomVisionSuperChargedPredictionRefreshCoordinator
{
    private const ulong MinRefreshIntervalMs = 250;
    private static int _suppressionDepth;
    private static bool _refreshScheduled;
    private static int _pendingRngConsumptions;
    private static ulong _lastRefreshTicks;
    private static string _lastSource = string.Empty;
    private static uint _lastSeed;
    private static int _lastCounter;

    public static IDisposable SuppressRngRefresh()
    {
        _suppressionDepth++;
        return new SuppressionScope();
    }

    public static void OnRngConsumed(Rng rng, string source)
    {
        if (_suppressionDepth > 0 || !HasActivePredictionWindow())
        {
            return;
        }

        _pendingRngConsumptions++;
        _lastSource = source;
        _lastSeed = rng.Seed;
        _lastCounter = rng.Counter;

        if (_refreshScheduled)
        {
            return;
        }

        ScheduleRefresh(source);
    }

    private static bool HasActivePredictionWindow()
    {
        return RandomVisionSuperChargedEventOverlay.HasActivePreview() ||
            RandomVisionSuperChargedMapEncounterOverlay.HasActivePreview();
    }

    private static void ScheduleRefresh(string source)
    {
        _refreshScheduled = true;

        var now = Time.GetTicksMsec();
        var elapsed = now >= _lastRefreshTicks ? now - _lastRefreshTicks : MinRefreshIntervalMs;
        var delayMs = elapsed >= MinRefreshIntervalMs ? 0 : MinRefreshIntervalMs - elapsed;

        MainFile.LogInfo(
            $"prediction-refresh scheduled source={source} delay-ms={delayMs} pending-rng={_pendingRngConsumptions}");

        if (delayMs == 0)
        {
            Callable.From(FlushScheduledRefresh).CallDeferred();
            return;
        }

        if (Engine.GetMainLoop() is SceneTree tree)
        {
            var timer = tree.CreateTimer(delayMs / 1000.0);
            timer.Timeout += FlushScheduledRefresh;
            return;
        }

        Callable.From(FlushScheduledRefresh).CallDeferred();
    }

    private static void FlushScheduledRefresh()
    {
        if (!_refreshScheduled)
        {
            return;
        }

        if (_suppressionDepth > 0)
        {
            _refreshScheduled = false;
            ScheduleRefresh("suppressed-refresh");
            return;
        }

        _refreshScheduled = false;
        var rngConsumptions = _pendingRngConsumptions;
        _pendingRngConsumptions = 0;

        if (!HasActivePredictionWindow())
        {
            MainFile.LogInfo(
                $"prediction-refresh skipped no-active-window rng-consumptions={rngConsumptions} last-source={_lastSource}");
            return;
        }

        var eventRefreshed = false;
        var mapRefreshed = false;
        using (SuppressRngRefresh())
        {
            eventRefreshed = RandomVisionSuperChargedEventOverlay.RefreshActiveFromRngChange();
            mapRefreshed = RandomVisionSuperChargedMapEncounterOverlay.RefreshActiveFromRngChange();
        }

        _lastRefreshTicks = Time.GetTicksMsec();
        MainFile.LogInfo(
            $"prediction-refresh done rng-consumptions={rngConsumptions} last-source={_lastSource} " +
            $"last-seed={_lastSeed} last-counter={_lastCounter} event={eventRefreshed} map={mapRefreshed}");

        if (_pendingRngConsumptions > 0 && HasActivePredictionWindow())
        {
            ScheduleRefresh("queued-during-refresh");
        }
    }

    private sealed class SuppressionScope : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _suppressionDepth = Math.Max(0, _suppressionDepth - 1);
        }
    }
}
