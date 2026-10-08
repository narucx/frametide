using System.Diagnostics;
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

public sealed record UndervoltOptions(int Target, int Step, int RoundSec, int MaxOffset, int TempLimit);

/// <summary>State of a running GPU test; written by the test thread, read by the UI.</summary>
public sealed class GpuTestProgress
{
    private readonly Lock _gate = new();
    private readonly List<StressRound> _rounds = [];

    public volatile string Status = "";
    public volatile string Warning = "";
    public volatile bool Done;
    public TestPhase Phase { get; set; } = TestPhase.Stock;
    public LiveReading? Live { get; set; }
    public GpuProfile? Result { get; set; }
    public StressRound? TestResult { get; set; }

    /// <summary>Power limit before the test, restored afterwards (also when the app exits during the test).</summary>
    public int? OriginalPowerLimitW { get; set; }

    public IReadOnlyList<StressRound> Rounds { get { lock (_gate) return [.. _rounds]; } }

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

    public static GpuTestProgress StartUndervolt(UndervoltOptions o) => Run((p, c) => Undervolt(p, o, c));

    public static GpuTestProgress StartProfileTest(string profile, int seconds, int tempLimit) =>
        Run((p, c) => ProfileTest(p, profile, seconds, tempLimit, c));

    public static void Cancel()
    {
        if (!Running) return;
        _cancel.Cancel();
        if (Current is { } p) p.Status = L.T("Cancelling ...");
    }

    /// <summary>Cancels a running test and waits for the GPU to be reset (app exit).</summary>
    public static void CancelAndWait(TimeSpan timeout)
    {
        if (!Running) return;
        Cancel();
        var sw = Stopwatch.StartNew();
        while (Running && sw.Elapsed < timeout) Thread.Sleep(100);
        if (Running) RestoreDefaults(Current);
    }

    private static GpuTestProgress Run(Action<GpuTestProgress, CancellationToken> test)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException(L.T("A GPU test is already running."));
        var progress = new GpuTestProgress { Status = L.T("Starting ...") };
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

        public void StartStress()
        {
            if (Stress.Start(TimeSpan.FromMinutes(1)) is { } err) throw new InvalidOperationException(L.T("The GPU stress test could not start: {0}", err));
        }

        /// <summary>After a driver reset: back to the last good offset, then restart the load (the stock reference is kept).</summary>
        public void Recover(int offset, int lockedClock)
        {
            Stress.Stop();
            Thread.Sleep(3000);
            Nvidia.Reconnect();
            Nvidia.SetClockOffset(offset);
            if (lockedClock > 0) Nvidia.LockClocks(GpuTuning.IdleMinClock, lockedClock);
            Thread.Sleep(3000);
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
        try { Nvidia.SetClockOffset(offset); }
        catch (InvalidOperationException e) { round.Reason = e.Message; return round; }
        var start = DateTime.Now;
        ctx.Stress.ResetErrors();
        Thread.Sleep(3000);   // settle after changing the offset, not measured
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
            Thread.Sleep(1000);
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

    private static void Undervolt(GpuTestProgress p, UndervoltOptions o, CancellationToken cancel)
    {
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
            p.OriginalPowerLimitW = Nvidia.GetSnapshot()?.PowerLimitW;
            GpuTuning.Reset(keepPowerLimit: true);
            Nvidia.SetPowerLimit(info.PowerMaxW);
            Say(p, "Stock: power limit temporarily {0} W, starting the built-in stress test ...", info.PowerMaxW);
            ctx.StartStress();
            var stock = Round(ctx, L.T("Stock"), 0, o.RoundSec);
            p.Add(stock);
            cancel.ThrowIfCancellationRequested();
            if (!stock.Stable) throw new InvalidOperationException(L.T("The stock baseline failed ({0}), so no undervolt was probed. Your GPU is not stable at default settings.", stock.Reason));

            var target = o.Target > 0 ? o.Target : stock.MedClock / 15 * 15;
            Say(p, "Stock: {0} MHz at {1} V, {2} W. Locking the clock to {3} MHz.", stock.MedClock, stock.Voltage, stock.AvgPower, target);
            Nvidia.LockClocks(GpuTuning.IdleMinClock, target);

            // PROBE: coarse steps until a failure, then one fine step in between.
            p.Phase = TestPhase.Probe;
            StressRound? good = null, bad = null;
            for (var offset = coarse; offset <= o.MaxOffset; offset += coarse)
            {
                var r = Round(ctx, L.T("Round {0}", p.RoundCount), offset, o.RoundSec, target);
                p.Add(r);
                cancel.ThrowIfCancellationRequested();
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

            // REPLAY: the best step twice as long, one step lower if needed.
            p.Phase = TestPhase.Replay;
            var final = good.Offset;
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
            p.Result = new GpuProfile
            {
                Name = $"UV {target} MHz{voltage} ({DateTime.Now:yyyy-MM-dd HH:mm})",
                MaxClock = target, OffsetMHz = final, PowerLimitW = 0, Gpu = info.Name, Driver = info.Driver,
                Created = new DateTime(DateTime.Now.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond),
                StockAvgPowerW = stock.AvgPower, StockAvgClock = stock.MedClock, StockVoltage = stock.Voltage,
                AvgPowerW = replay.AvgPower, AvgClock = replay.MedClock, Voltage = replay.Voltage, PowerSavedW = saved,
                VoltageDropMv = stock.Voltage > 0 && replay.Voltage > 0 ? Math.Round((stock.Voltage - replay.Voltage) * 1000) : null,
                PowerSavedPct = stock.AvgPower > 0 ? Math.Round(100 * saved / stock.AvgPower, 1) : 0, MaxTemp = p.MaxTemp,
            };
            p.Phase = TestPhase.Done;
            Say(p, "Done: {0} MHz at {1} V instead of {2} V. Power {3} W -> {4} W.", target, replay.Voltage, stock.Voltage, stock.AvgPower, replay.AvgPower);
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
            GpuTuning.Reset(keepPowerLimit: true);
            Say(p, "Creating the reference at stock settings ...");
            ctx.StartStress();
            Thread.Sleep(2000);
            GpuTuning.Apply(profile.MaxClock, profile.OffsetMHz, profile.PowerLimitW);
            Say(p, "Testing profile '{0}' for {1} s ...", profile.Name, seconds);
            var r = Round(ctx, L.T("Test"), profile.OffsetMHz, seconds, profile.MaxClock);
            p.TestResult = r;
            passed = r.Stable;
            if (passed) Say(p, "Profile '{0}' passed: {1} MHz at {2} V, {3} W, max {4} °C, no errors. The profile stays active.", profile.Name, r.MedClock, r.Voltage, r.AvgPower, r.MaxTemp);
            else Say(p, "Profile '{0}' FAILED: {1} The GPU was reset to default.", profile.Name, r.Reason);
        }
        catch (InvalidOperationException e) { Say(p, "Aborted: {0}", e.Message); }
        finally
        {
            ctx.Stress.Dispose();
            if (passed)
            {
                try { GpuTuning.ApplyProfile(name); }
                catch (InvalidOperationException e) { Log.Warn(e.Message); RestoreDefaults(p); }
            }
            else RestoreDefaults(p);
        }
    }

    /// <summary>Back to default clocks and the power limit from before the test.</summary>
    public static void RestoreDefaults(GpuTestProgress? p)
    {
        try
        {
            GpuTuning.Reset(keepPowerLimit: true);
            GpuTuning.SetActive("");
            if (p?.OriginalPowerLimitW is { } w) Nvidia.SetPowerLimit(w);
        }
        catch (InvalidOperationException e) { Log.Warn($"Resetting the GPU: {e.Message}"); }
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
