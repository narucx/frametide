using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Core.Tweaks;

public enum TweakStatus { NotApplied, Applied, Partial, NotAvailable, Error }

public enum Risk { Safe, Moderate, Risky }

public enum RestartNeed { None, SignOut, Reboot }

/// <summary>A registry value a tweak sets. Path in registry drive syntax ("HKLM:\...").</summary>
public sealed record RegSetting(string Path, string Name, RegistryValueKind Kind, object Value)
{
    public string JournalKey => $"reg|{Path}|{Name}";
}

public sealed record ServiceSetting(string Name, StartMode Start)
{
    public string JournalKey => $"svc|{Name}";
}

/// <summary>
/// A single optimization that can be switched on and off. Names and descriptions are English; the UI translates them.
/// Before changing anything, a tweak saves the original value in the journal (first time only).
/// </summary>
public abstract class Tweak
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public Risk Risk { get; init; } = Risk.Safe;
    public bool Recommended { get; init; }
    public RestartNeed Restart { get; init; } = RestartNeed.None;

    /// <summary>Shown when Windows denies the change (e.g. protected policy keys).</summary>
    public string? BlockedHint { get; init; }

    public abstract TweakStatus GetStatus();
    public abstract void Apply(Journal journal);
    public abstract void Revert(Journal journal);
}

/// <summary>Sets registry values and service start modes; status, backup and revert are generic.</summary>
public sealed class RegistryTweak : Tweak
{
    public IReadOnlyList<RegSetting> Registry { get; init; } = [];
    public IReadOnlyList<ServiceSetting> Services { get; init; } = [];

    /// <summary>Optional: decides the status first (e.g. "not available on this PC"); null = use the generic check.</summary>
    public Func<TweakStatus?>? StatusOverride { get; init; }

    public override TweakStatus GetStatus()
    {
        if (StatusOverride?.Invoke() is { } s) return s;
        int total = 0, ok = 0;
        foreach (var r in Registry)
        {
            total++;
            if (Reg.Equals(r.Path, r.Name, r.Kind, r.Value)) ok++;
        }
        foreach (var svc in Services)
        {
            var cur = Windows.Services.GetStartMode(svc.Name);
            if (cur is null) continue;              // service does not exist on this PC
            total++;
            if (cur == svc.Start) ok++;
        }
        if (total == 0) return TweakStatus.NotAvailable;
        return ok == total ? TweakStatus.Applied : ok > 0 ? TweakStatus.Partial : TweakStatus.NotApplied;
    }

    public override void Apply(Journal journal)
    {
        foreach (var r in Registry)
        {
            journal.SaveOriginal(Id, r.JournalKey, Reg.Get(r.Path, r.Name).ToJson());
            Reg.Set(r.Path, r.Name, r.Kind, r.Value);
        }
        foreach (var svc in Services)
        {
            var cur = Windows.Services.GetStartMode(svc.Name);
            if (cur is null) continue;
            journal.SaveOriginal(Id, svc.JournalKey, cur.Value.ToString());
            Windows.Services.SetStartMode(svc.Name, svc.Start);
        }
    }

    public override void Revert(Journal journal)
    {
        var entry = journal.Get(Id);
        foreach (var r in Registry)
        {
            // No backup, or the value did not exist before -> remove it = Windows default.
            Reg.Restore(r.Path, r.Name, RegValue.FromJson(entry?[r.JournalKey]));
        }
        foreach (var svc in Services)
        {
            if (Windows.Services.GetStartMode(svc.Name) is null) continue;
            var original = (string?)entry?[svc.JournalKey];
            var mode = Enum.TryParse<StartMode>(original, out var m) && m is not (StartMode.Boot or StartMode.System) ? m : StartMode.Manual;
            Windows.Services.SetStartMode(svc.Name, mode);
        }
    }
}

/// <summary>A tweak with its own logic. Apply/Revert do their own journal backup.</summary>
public sealed class CustomTweak : Tweak
{
    public required Func<TweakStatus> Status { get; init; }
    public required Action<Journal> ApplyAction { get; init; }
    public required Action<Journal> RevertAction { get; init; }

    public override TweakStatus GetStatus() => Status();
    public override void Apply(Journal journal) => ApplyAction(journal);
    public override void Revert(Journal journal) => RevertAction(journal);
}

/// <summary>Detects a harmful tweak set by another tool and restores the Windows default.</summary>
public sealed class Repair
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Returns null when everything is fine, otherwise what was found.</summary>
    public required Func<string?> Detect { get; init; }
    public required Action Fix { get; init; }
}
