using Frametide.Core.Infrastructure;

namespace Frametide.Core.Tweaks;

public sealed record TweakResult(bool Success, TweakStatus Status, string? Error, string? Hint);

public sealed record RepairFinding(Repair Repair, string Message);

/// <summary>Applies and reverts tweaks. The result of an apply is always verified, never just assumed.</summary>
public sealed class TweakEngine(IReadOnlyList<Tweak> tweaks, IReadOnlyList<Repair> repairs, Journal journal)
{
    public static TweakEngine Default => new(TweakCatalog.Tweaks, TweakCatalog.Repairs, Journal.Default);

    public IReadOnlyList<Tweak> Tweaks { get; } = tweaks;
    public IReadOnlyList<Repair> Repairs { get; } = repairs;

    public Tweak? Find(string id) => Tweaks.FirstOrDefault(t => t.Id == id);

    public TweakStatus GetStatus(Tweak tweak)
    {
        try { return tweak.GetStatus(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warn($"Status of {tweak.Name}: {e.Message}");
            return TweakStatus.Error;
        }
    }

    public TweakResult Apply(Tweak tweak)
    {
        Log.Info($"Apply: {tweak.Name}");
        Exception? error = null;
        try { tweak.Apply(journal); }
        catch (Exception e) when (e is not OutOfMemoryException) { error = e; }

        var status = GetStatus(tweak);
        if (status is TweakStatus.Applied or TweakStatus.NotAvailable)
        {
            Log.Ok($"Applied: {tweak.Name}");
            return new TweakResult(true, status, null, null);
        }
        if (error is not null)
        {
            var denied = error is UnauthorizedAccessException or System.Security.SecurityException;
            var hint = denied ? tweak.BlockedHint : null;
            Log.Error($"Failed: {tweak.Name}: {error.Message}");
            if (hint is not null) Log.Warn(hint);
            return new TweakResult(false, status, error.Message, hint);
        }
        Log.Warn($"{tweak.Name}: no error, but Windows does not report it as active (status: {status}). It may need a restart.");
        return new TweakResult(false, status, null, null);
    }

    public bool Revert(Tweak tweak)
    {
        Log.Info($"Revert: {tweak.Name}");
        try
        {
            tweak.Revert(journal);
            journal.Remove(tweak.Id);
            Log.Ok($"Reverted: {tweak.Name}");
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"Error reverting {tweak.Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Reverts every tweak that has a journal entry.</summary>
    public void RevertAll()
    {
        foreach (var id in journal.TweakIds)
        {
            if (Find(id) is { } t) Revert(t);
            else Log.Warn($"Journal entry '{id}' belongs to no known tweak, left as is.");
        }
    }

    public void ApplyProfile(TweakProfile profile)
    {
        Log.Info($"Applying profile: {profile.Name}");
        foreach (var id in profile.TweakIds)
        {
            if (Find(id) is not { } t) { Log.Warn($"Unknown tweak in profile: {id}"); continue; }
            if (GetStatus(t) == TweakStatus.Applied) continue;
            Apply(t);
        }
        Log.Ok($"Profile '{profile.Name}' done.");
    }

    public IReadOnlyList<RepairFinding> FindRepairs()
    {
        var list = new List<RepairFinding>();
        foreach (var r in Repairs)
        {
            try { if (r.Detect() is { } msg) list.Add(new RepairFinding(r, msg)); }
            catch (Exception e) when (e is not OutOfMemoryException) { Log.Warn($"Check '{r.Name}': {e.Message}"); }
        }
        return list;
    }

    public bool Fix(Repair repair)
    {
        try
        {
            repair.Fix();
            Log.Ok($"Repaired: {repair.Name}");
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"Repair '{repair.Name}' failed: {e.Message}");
            return false;
        }
    }
}
