using System.Diagnostics;
using System.Text.Json;
using Frametide.Core.Hardware;
using Frametide.Core.Infrastructure;

namespace Frametide.Core.Gpu;

/// <summary>One measured round under the stress load.</summary>
public sealed class StressRound
{
    public string Label { get; init; } = "";
    public int Offset { get; init; }
    public int AvgClock { get; set; }
    public int MedClock { get; set; }
    public int PeakClock { get; set; }
    public double AvgPower { get; set; }
    public double Voltage { get; set; }
    public int MaxTemp { get; set; }
    public long Errors { get; set; }
    public bool Stable { get; set; }
    public string Reason { get; set; } = "";
    public bool DeviceLost { get; set; }
    public bool Thermal { get; set; }
}

public sealed record LiveReading(int Clock, double Voltage, double Power, int Temp, long Errors, string Label, int Offset, int Elapsed, int Seconds);

public enum TestPhase { Stock, Probe, Replay, Done, Failed }

public enum GpuTestKind { Undervolt, ProfileTest }

public sealed record UndervoltOptions(int Target, int Step, int RoundSec, int MaxOffset, int TempLimit);

/// <summary>Written to the data folder while a test has the GPU changed; a later start resets the GPU when it is still there.</summary>
internal sealed record GpuTestMarker(int OriginalPowerLimitW, DateTimeOffset Started, int Pid);

internal enum MarkerAction { Wait, Discard, Recover }

/// <summary>State of a running GPU test; written by the test thread, read by the UI.</summary>
public sealed class GpuTestProgress
{
    private readonly Lock _gate = new();
    private readonly List<StressRound> _rounds = [];

    public volatile string Status = "";
    public volatile string Warning = "";
    public volatile bool Done;
    public GpuTestKind Kind { get; init; }
    public TestPhase Phase { get; set; } = TestPhase.Stock;
    public LiveReading? Live { get; set; }
    public GpuProfile? Result { get; set; }
    public StressRound? TestResult { get; set; }

    /// <summary>Power limit before the test, restored afterwards (also when the app exits during the test).</summary>
    public int? OriginalPowerLimitW { get; set; }

    public IReadOnlyList<StressRound> Rounds { get { lock (_gate) return [.. _rounds]; } }

    /// <summary>Held by the test thread while it changes the GPU and by <see cref="GpuTests.CancelAndWait"/> while it restores.</summary>
    internal Lock GpuGate { get; } = new();

    /// <summary>Set once the GPU was restored from outside the test thread: the test thread must not change the GPU any more.</summary>
    internal volatile bool Frozen;

    internal void Add(StressRound r) { lock (_gate) _rounds.Add(r); }

    internal int RoundCount { get { lock (_gate) return _rounds.Count; } }

    internal int MaxTemp { get { lock (_gate) return _rounds.Count > 0 ? _rounds.Max(r => r.MaxTemp) : 0; } }
}

/// <summary>
/// Smart auto undervolt and profile stress test (NVIDIA, NVML + the built-in stress test).
/// STOCK: power limit temporarily at max, stress test at stock: sustained boost clock, voltage, reference results.
/// PROBE: clock locked to that stock clock, the V/F curve shifted up step by step: same clock at a lower voltage. Every
/// step runs under full load; unstable = computation errors, a driver reset or a driver crash event.
/// REPLAY: the best step is validated with a longer run; only then a profile is created.
/// </summary>
public static class GpuTests
{
    private const string ThreadName = "Frametide GPU test";
    private static int _running;
    private static readonly string[] OtherTuningTools = ["MSIAfterburner", "EVGAPrecisionX", "GPUTweak", "TinyBoost"];

    public static bool Running => Volatile.Read(ref _running) != 0;

    /// <summary>The running or last test.</summary>
    public static GpuTestProgress? Current { get; private set; }

    private static CancellationTokenSource _cancel = new();

    internal static string MarkerPath => Path.Combine(AppPaths.DataDir, "gpu-test.json");

    public static GpuTestProgress StartUndervolt(UndervoltOptions o) => Run(GpuTestKind.Undervolt, (p, c) => Undervolt(p, o, c));

    public static GpuTestProgress StartProfileTest(string profile, int seconds, int tempLimit) =>
        Run(GpuTestKind.ProfileTest, (p, c) => ProfileTest(p, profile, seconds, tempLimit, c));

    public static void Cancel()
    {
        if (!Running) return;
        _cancel.Cancel();
        if (Current is { } p) p.Status = L.T("Cancelling ...");
    }

    /// <summary>
    /// Cancels a running test and waits for the GPU to be reset (app exit). When the test does not end in time, the GPU
    /// is reset from here and the test thread can no longer change it. TimeSpan.Zero = reset right away (crash handler).
    /// </summary>
    public static void CancelAndWait(TimeSpan timeout)
    {
        if (!Running) return;
        Cancel();
        var sw = Stopwatch.StartNew();
        while (Running && sw.Elapsed < timeout) Thread.Sleep(100);
        if (!Running || Current is not { } p) return;
        // The test thread may be inside an NVML call that hangs (driver reset): do not wait for it forever.
        var locked = p.GpuGate.TryEnter(TimeSpan.FromSeconds(5));
        try
        {
            p.Frozen = true;
            RestoreDefaults(p);
        }
        finally { if (locked) p.GpuGate.Exit(); }
    }

    private static GpuTestProgress Run(GpuTestKind kind, Action<GpuTestProgress, CancellationToken> test)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException(L.T("A GPU test is already running."));
        var progress = new GpuTestProgress { Kind = kind, Status = L.T("Starting ...") };
        Current = progress;
        _cancel.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        var thread = new Thread(() =>
        {
            try { test(progress, token); }
            catch (Exception e)   // anything escaping a worker thread would end the whole app
            {
                progress.Phase = TestPhase.Failed;
                progress.Status = L.T("Aborted: {0}", e.Message);
                Log.Error($"GPU test: {e}");
                RestoreDefaults(progress);
            }
            finally
            {
                progress.Live = null;
                progress.Done = true;
                Volatile.Write(ref _running, 0);
            }
        }) { IsBackground = true, Name = ThreadName };
        thread.Start();
        return progress;
    }

    private sealed class Context(GpuTestProgress progress, int tempLimit, CancellationToken cancel)
    {
        public GpuTestProgress Progress { get; } = progress;
        public int TempLimit { get; } = tempLimit;
        public CancellationToken Cancel { get; } = cancel;
        public GpuStress Stress { get; } = new();

        /// <summary>
        /// Every GPU change of the test thread goes through here: after <see cref="CancelAndWait"/> restored the GPU
        /// (frozen), a late test thread must not set an offset again.
        /// </summary>
        public void Gpu(Action change)
        {
            lock (Progress.GpuGate)
            {
                if (Progress.Frozen) throw new OperationCanceledException();
                change();
            }
        }

        /// <summary>Waits, returns early when cancelled. False = cancelled.</summary>
        public bool Wait(int ms) => !Cancel.WaitHandle.WaitOne(ms);

        public void StartStress()
        {
            if (Stress.Start(TimeSpan.FromMinutes(1)) is { } err) throw new InvalidOperationException(L.T("The GPU stress test could not start: {0}", err));
        }

        /// <summary>After a driver reset: back to the last good offset, then restart the load (the stock reference is kept).</summary>
        public void Recover(int offset, int lockedClock)
        {
            Cancel.ThrowIfCancellationRequested();
            Stress.Stop();
            if (!Wait(3000)) Cancel.ThrowIfCancellationRequested();
            Nvidia.Reconnect();
            Gpu(() =>
            {
                if (lockedClock > 0) Nvidia.LockClocks(GpuTuning.IdleMinClock, lockedClock);
                Nvidia.SetClockOffset(offset);
            });
            if (!Wait(3000)) Cancel.ThrowIfCancellationRequested();
            StartStress();
        }
    }

    private static void Say(GpuTestProgress p, string text, params object?[] args)
    {
        p.Status = L.T(text, args);
        Log.Info("[GPU test] " + string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args));
    }

    private static StressRound Round(Context ctx, string label, int offset, int seconds, int expectClock = 0)
    {
        var round = new StressRound { Label = label, Offset = offset };
        if (ctx.Cancel.IsCancellationRequested) { round.Reason = L.T("Cancelled"); return round; }
        try { ctx.Gpu(() => Nvidia.SetClockOffset(offset)); }
        catch (InvalidOperationException e) { round.Reason = e.Message; return round; }
        var start = DateTime.Now;
        ctx.Stress.ResetErrors();
        if (!ctx.Wait(3000)) { round.Reason = L.T("Cancelled"); return round; }   // settle after changing the offset, not measured
        ctx.Stress.ResetErrors();
        var measure = Stopwatch.StartNew();
        var clocks = new List<double>();
        var powers = new List<double>();
        var volts = new List<double>();
        while (measure.Elapsed.TotalSeconds < seconds)
        {
            if (ctx.Cancel.IsCancellationRequested) { round.Reason = L.T("Cancelled"); return round; }
            if (ctx.Stress.DeviceLost || !ctx.Stress.Running)
            {
                round.Reason = L.T("Driver reset / GPU device lost ({0})", ctx.Stress.Status);
                round.DeviceLost = true;
                return round;
            }
            if (Nvidia.GetSnapshot() is not { } s)
            {
                round.Reason = L.T("GPU not responding (driver reset?)");
                round.DeviceLost = true;
                Nvidia.Reconnect();
                return round;
            }
            round.MaxTemp = Math.Max(round.MaxTemp, s.TempC);
            if (s.TempC >= ctx.TempLimit) { round.Reason = L.T("Temperature limit reached ({0} °C)", s.TempC); round.Thermal = true; return round; }
            round.Errors = ctx.Stress.Errors;
            if (round.Errors > 0) { round.Reason = L.T("{0} computation errors (artifacts)", round.Errors); return round; }
            if (s.Util >= 80)
            {
                clocks.Add(s.ClockMHz);
                powers.Add(s.PowerW);
                if (s.VoltageV > 0) volts.Add(s.VoltageV);
            }
            ctx.Progress.Live = new LiveReading(s.ClockMHz, s.VoltageV, s.PowerW, s.TempC, round.Errors, label, offset, (int)measure.Elapsed.TotalSeconds, seconds);
            ctx.Wait(1000);
        }
        if (DriverEvents.CrashSince(start) is { } crash) { round.Reason = L.T("Driver crash: {0}", crash); return round; }
        if (clocks.Count < Math.Max(3, seconds / 3)) { round.Reason = L.T("Too few samples under load"); return round; }
        round.AvgClock = (int)clocks.Average();
        round.MedClock = (int)Median(clocks);
        round.PeakClock = (int)clocks.Max();
        round.AvgPower = Math.Round(powers.Average(), 1);
        if (volts.Count > 0) round.Voltage = Math.Round(Median(volts), 3);
        if (expectClock > 0 && round.MedClock < expectClock - 45)
        {
            round.Reason = L.T("Clock could not be held ({0} of {1} MHz)", round.MedClock, expectClock);
            return round;
        }
        round.Stable = true;
        return round;
    }

    internal static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var s = values.Order().ToList();
        return s[(s.Count - 1) / 2];
    }

    /// <summary>
    /// Remembers the power limit to restore and writes the crash marker. Runs before the test changes anything: without a
    /// known original power limit the test does not start (it would stay at the maximum).
    /// </summary>
    private static void Begin(GpuTestProgress p, GpuInfo info)
    {
        var original = Nvidia.GetPowerLimitW() ?? (info.PowerDefaultW > 0 ? info.PowerDefaultW : (int?)null)
            ?? throw new InvalidOperationException(L.T("The current power limit could not be read, so the test was not started."));
        p.OriginalPowerLimitW = original;
        try { JsonFile.Write(MarkerPath, new GpuTestMarker(original, DateTimeOffset.Now, Environment.ProcessId)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(L.T("The test could not write to the data folder: {0}", e.Message));
        }
    }

    /// <summary>The search never goes further than this, whatever the user enters.</summary>
    public const int MaxSafeOffset = 450;

    /// <summary>Below this voltage the search stops, even if the GPU still passes the stress test.</summary>
    public const double MinSafeVoltage = 0.85;

    private static void Undervolt(GpuTestProgress p, UndervoltOptions o, CancellationToken cancel)
    {
        o = o with { MaxOffset = Math.Clamp(o.MaxOffset, o.Step, MaxSafeOffset) };
        var ctx = new Context(p, o.TempLimit, cancel);
        try
        {
            var info = Nvidia.GetInfo() ?? throw new InvalidOperationException(L.T("No NVIDIA GPU (NVML) found."));
            if (!info.OffsetSupported) throw new InvalidOperationException(L.T("The driver does not support clock offsets through NVML."));
            if (RunningTuningTool() is { } tool)
            {
                Log.Warn($"Other tuning tool running: {tool}");
                p.Warning = L.T("Another GPU tuning tool is running ({0}). Close it, it can overwrite the settings during the test.", tool);
            }
            var coarse = o.Step * 2;

            // STOCK
            p.Phase = TestPhase.Stock;
            Begin(p, info);
            cancel.ThrowIfCancellationRequested();
            ctx.Gpu(() =>
            {
                GpuTuning.ResetCore(keepPowerLimit: true);
                Nvidia.SetPowerLimit(info.PowerMaxW);
            });
            Say(p, "Stock: power limit temporarily {0} W, starting the built-in stress test ...", info.PowerMaxW);
            ctx.StartStress();
            var stock = Round(ctx, L.T("Stock"), 0, o.RoundSec);
            p.Add(stock);
            cancel.ThrowIfCancellationRequested();
            if (!stock.Stable) throw new InvalidOperationException(L.T("The stock baseline failed ({0}), so no undervolt was probed. Your GPU is not stable at default settings.", stock.Reason));

            var target = o.Target > 0 ? o.Target : stock.MedClock / 15 * 15;
            Say(p, "Stock: {0} MHz at {1} V, {2} W. Locking the clock to {3} MHz.", stock.MedClock, stock.Voltage, stock.AvgPower, target);
            ctx.Gpu(() => Nvidia.LockClocks(GpuTuning.IdleMinClock, target));

            // PROBE: coarse steps until a failure, then one fine step in between.
            p.Phase = TestPhase.Probe;
            StressRound? good = null, bad = null;
            var floorReached = false;
            for (var offset = coarse; offset <= o.MaxOffset; offset += coarse)
            {
                var r = Round(ctx, L.T("Round {0}", p.RoundCount), offset, o.RoundSec, target);
                p.Add(r);
                cancel.ThrowIfCancellationRequested();
                if (r.Stable && r.Voltage is > 0 and < MinSafeVoltage)
                {
                    Say(p, "Stable at +{0} MHz, but {1} V is below the safe minimum of {2} V: stopping here.", offset, r.Voltage, MinSafeVoltage);
                    floorReached = true;
                    break;
                }
                if (r.Stable)
                {
                    good = r;
                    Say(p, "Stable at +{0} MHz: {1} V, {2} W.", offset, r.Voltage, r.AvgPower);
                    continue;
                }
                bad = r;
                Say(p, "Unstable at +{0} MHz: {1}", offset, r.Reason);
                if (!r.Thermal && (r.DeviceLost || !ctx.Stress.Running)) ctx.Recover(good?.Offset ?? 0, target);
                break;
            }
            // Stable up to the limit: the GPU's real limit was not found, more may be possible.
            if (bad is null && good is not null && !floorReached && good.Offset + coarse <= MaxSafeOffset)
                Say(p, "Stable up to the maximum offset (+{0} MHz) without errors. Raise MAX OFFSET to look for more.", good.Offset);
            if (bad is { Thermal: false })
            {
                var mid = (good?.Offset ?? 0) + o.Step;
                if (mid < bad.Offset)
                {
                    var r = Round(ctx, L.T("Round {0}", p.RoundCount), mid, o.RoundSec, target);
                    p.Add(r);
                    cancel.ThrowIfCancellationRequested();
                    if (r.Stable) good = r;
                    else if (r.DeviceLost || !ctx.Stress.Running) ctx.Recover(good?.Offset ?? 0, target);
                }
            }
            if (good is null) throw new InvalidOperationException(L.T("No stable undervolt step found. Your GPU already runs close to its limit at this clock."));

            // REPLAY: the best step twice as long, one step lower if needed. When the search found the GPU's limit
            // (computation errors, driver reset), one step of safety margin: games load the GPU differently.
            p.Phase = TestPhase.Replay;
            var final = good.Offset;
            if (bad is { Thermal: false } && final - o.Step > 0)
            {
                final -= o.Step;
                Say(p, "Limit found at +{0} MHz. Safety margin: using +{1} MHz.", good.Offset, final);
            }
            StressRound? replay = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Say(p, "Replay: validating +{0} MHz for {1} s ...", final, o.RoundSec * 2);
                replay = Round(ctx, L.T("Replay"), final, o.RoundSec * 2, target);
                p.Add(replay);
                cancel.ThrowIfCancellationRequested();
                if (replay.Stable) break;
                if (replay.DeviceLost || !ctx.Stress.Running) ctx.Recover(0, target);
                final -= o.Step;
                if (final <= 0) throw new InvalidOperationException(L.T("Replay failed even close to stock: {0}", replay.Reason));
            }
            if (replay is not { Stable: true }) throw new InvalidOperationException(L.T("Replay failed: {0}", replay?.Reason));

            var saved = Math.Round(stock.AvgPower - replay.AvgPower);
            var voltage = replay.Voltage > 0 ? $" @ {replay.Voltage:N3} V" : "";
            var result = new GpuProfile
            {
                Name = $"UV {target} MHz{voltage} ({DateTime.Now:yyyy-MM-dd HH:mm})",
                MaxClock = target, OffsetMHz = final, PowerLimitW = 0, Gpu = info.Name, Driver = info.Driver,
                Created = new DateTime(DateTime.Now.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond),
                StockAvgPowerW = stock.AvgPower, StockAvgClock = stock.MedClock, StockVoltage = stock.Voltage,
                AvgPowerW = replay.AvgPower, AvgClock = replay.MedClock, Voltage = replay.Voltage, PowerSavedW = saved,
                VoltageDropMv = stock.Voltage > 0 && replay.Voltage > 0 ? Math.Round((stock.Voltage - replay.Voltage) * 1000) : null,
                PowerSavedPct = stock.AvgPower > 0 ? Math.Round(100 * saved / stock.AvgPower, 1) : 0, MaxTemp = p.MaxTemp,
            };
            // Saved here, not by the page: the window may be closed to the tray while the test runs.
            try { GpuProfiles.Save(result); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(L.T("The profile could not be saved: {0}", e.Message));
            }
            p.Result = result;
            p.Phase = TestPhase.Done;
            Say(p, "Done: {0} MHz at {1} V instead of {2} V. Power {3} W -> {4} W.", target, replay.Voltage, stock.Voltage, stock.AvgPower, replay.AvgPower);
            if (bad is null && !floorReached && final == good.Offset && good.Offset + coarse <= MaxSafeOffset)
            {
                const string limited = "Limited by MAX OFFSET, not by the GPU: a higher MAX OFFSET may give a lower voltage.";
                p.Status += " " + L.T(limited);
                Log.Info("[GPU test] " + limited);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
        {
            p.Phase = TestPhase.Failed;
            Say(p, "Aborted: {0}", e is OperationCanceledException ? L.T("Cancelled.") : e.Message);
        }
        finally
        {
            ctx.Stress.Dispose();
            RestoreDefaults(p);
        }
    }

    /// <summary>Stock reference, then the profile under load for the given time. Passed: the profile stays applied.</summary>
    private static void ProfileTest(GpuTestProgress p, string name, int seconds, int tempLimit, CancellationToken cancel)
    {
        var ctx = new Context(p, tempLimit, cancel);
        var passed = false;
        try
        {
            var profile = GpuProfiles.Find(name) ?? throw new InvalidOperationException(L.T("GPU profile '{0}' not found.", name));
            var info = Nvidia.GetInfo() ?? throw new InvalidOperationException(L.T("No NVIDIA GPU (NVML) found."));
            GpuTuning.EnsureSameGpu(profile, info);
            Begin(p, info);
            cancel.ThrowIfCancellationRequested();
            ctx.Gpu(() => GpuTuning.ResetCore(keepPowerLimit: true));
            Say(p, "Creating the reference at stock settings ...");
            ctx.StartStress();
            if (!ctx.Wait(2000)) throw new OperationCanceledException();
            ctx.Gpu(() => GpuTuning.ApplyCore(profile.MaxClock, profile.OffsetMHz, profile.PowerLimitW));
            Say(p, "Testing profile '{0}' for {1} s ...", profile.Name, seconds);
            var r = Round(ctx, L.T("Test"), profile.OffsetMHz, seconds, profile.MaxClock);
            p.TestResult = r;
            passed = r.Stable && !cancel.IsCancellationRequested;
            if (passed) Say(p, "Profile '{0}' passed: {1} MHz at {2} V, {3} W, max {4} °C, no errors. The profile stays active.", profile.Name, r.MedClock, r.Voltage, r.AvgPower, r.MaxTemp);
            else Say(p, "Profile '{0}' FAILED: {1} The GPU was reset to default.", profile.Name, r.Reason);
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
        {
            Say(p, "Aborted: {0}", e is OperationCanceledException ? L.T("Cancelled.") : e.Message);
        }
        finally
        {
            ctx.Stress.Dispose();
            if (passed)
            {
                try
                {
                    ctx.Gpu(() => GpuTuning.ApplyProfileCore(name, unattended: false));
                    DeleteMarker();
                }
                catch (Exception e) when (e is InvalidOperationException or OperationCanceledException) { Log.Warn(e.Message); RestoreDefaults(p); }
            }
            else RestoreDefaults(p);
        }
    }

    /// <summary>
    /// Back to default clocks and the power limit from before the test. Safe to call from any thread, also while the
    /// test thread is still running. The crash marker is removed once everything was restored.
    /// </summary>
    public static void RestoreDefaults(GpuTestProgress? p)
    {
        try
        {
            var ok = GpuTuning.ResetCore(keepPowerLimit: true);
            GpuTuning.SetActive("");
            if ((p?.OriginalPowerLimitW ?? ReadMarker()?.OriginalPowerLimitW) is { } w and > 0)
            {
                try { Nvidia.SetPowerLimit(w); }
                catch (InvalidOperationException e) { Log.Warn($"Restoring the power limit: {e.Message}"); ok = false; }
            }
            if (ok) DeleteMarker();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { Log.Warn($"Resetting the GPU: {e.Message}"); }
    }

    /// <summary>
    /// Call once at start (app and sign-in task). When a test of this boot did not finish (crash, killed process), the
    /// GPU still has its offset, clock lock and maximum power limit: reset them and restore the power limit from before
    /// the test. Returns true when the GPU was reset. Never throws.
    /// </summary>
    public static bool RecoverAfterCrash()
    {
        try
        {
            if (Running) return false;
            GpuTestMarker? marker;
            try { marker = JsonFile.Read<GpuTestMarker>(MarkerPath); }
            catch (JsonException e) { Log.Warn($"GPU test marker unreadable, removed: {e.Message}"); DeleteMarker(); return false; }
            if (marker is null) return false;
            switch (Evaluate(marker, GpuTuning.LastBoot, OwnerAlive(marker)))
            {
                case MarkerAction.Wait:
                    return false;
                case MarkerAction.Discard:
                    DeleteMarker();   // from an earlier boot: the GPU runs at defaults again
                    return false;
            }
            Log.Warn($"A GPU test started at {marker.Started:yyyy-MM-dd HH:mm:ss} did not finish. Resetting the GPU and restoring the power limit ({marker.OriginalPowerLimitW} W).");
            var ok = GpuTuning.ResetCore(keepPowerLimit: true);
            GpuTuning.SetActive("");
            if (marker.OriginalPowerLimitW > 0)
            {
                try { Nvidia.SetPowerLimit(marker.OriginalPowerLimitW); }
                catch (InvalidOperationException e) { Log.Warn($"Restoring the power limit: {e.Message}"); ok = false; }
            }
            if (ok) DeleteMarker();
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"GPU recovery after an unfinished test: {e.Message}");
            return false;
        }
    }

    /// <summary>What to do with a marker found at start: the test still runs in another process, it is stale, or the GPU needs a reset.</summary>
    internal static MarkerAction Evaluate(GpuTestMarker marker, DateTimeOffset lastBoot, bool ownerAlive)
    {
        if (ownerAlive) return MarkerAction.Wait;
        return marker.Started < lastBoot ? MarkerAction.Discard : MarkerAction.Recover;
    }

    /// <summary>The Frametide process that wrote the marker still runs (and may still be testing).</summary>
    private static bool OwnerAlive(GpuTestMarker marker)
    {
        if (marker.Pid == Environment.ProcessId) return false;   // this process: Running is false, so the test is over
        try
        {
            using var proc = Process.GetProcessById(marker.Pid);
            using var self = Process.GetCurrentProcess();
            // PIDs are reused: same program and started before the marker was written.
            return proc.ProcessName == self.ProcessName && proc.StartTime <= marker.Started.LocalDateTime;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static GpuTestMarker? ReadMarker()
    {
        try { return JsonFile.Read<GpuTestMarker>(MarkerPath); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static void DeleteMarker()
    {
        try { JsonFile.Delete(MarkerPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"GPU test marker: {e.Message}"); }
    }

    private static string? RunningTuningTool()
    {
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
                if (OtherTuningTools.FirstOrDefault(t => proc.ProcessName.StartsWith(t, StringComparison.OrdinalIgnoreCase)) is { } tool) return tool;
        }
        return null;
    }
}
