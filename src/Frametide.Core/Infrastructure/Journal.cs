using System.Text.Json;
using System.Text.Json.Nodes;

namespace Frametide.Core.Infrastructure;

/// <summary>
/// Backup of original values: { "tweak id": { "key": original value } }. Before a tweak changes anything it saves
/// the original here, and only the first time, so applying twice never stores an already-changed value.
/// "Revert" restores exactly these values.
/// Key conventions: "reg|HKLM:\path|name" -> {Exists, Type, Value}; "key|HKLM:\path" -> whether the key existed;
/// "svc|name" -> start mode; power plan GUID -> {AC, DC}; "pnp|class key" -> {Exists, Value};
/// "nic|NetCfgInstanceId|keyword" -> {Exists, Type, Value}.
/// </summary>
public sealed class Journal(string path)
{
    private static readonly object Gate = new();

    public static Journal Default => new(AppPaths.Journal);

    public string FilePath { get; } = path;

    public IReadOnlyList<string> TweakIds
    {
        get { lock (Gate) return Load().Select(p => p.Key).ToList(); }
    }

    /// <summary>Copy of the saved originals of a tweak, or null.</summary>
    public JsonObject? Get(string tweakId)
    {
        lock (Gate) return Load()[tweakId]?.DeepClone() as JsonObject;
    }

    public JsonNode? GetOriginal(string tweakId, string key)
    {
        lock (Gate) return Load()[tweakId]?[key]?.DeepClone();
    }

    /// <summary>Stores the original value unless one is stored already. Returns true when it was stored now.</summary>
    public bool SaveOriginal(string tweakId, string key, JsonNode? value)
    {
        lock (Gate)
        {
            var root = Load();
            if (root[tweakId] is not JsonObject entry) { entry = []; root[tweakId] = entry; }
            if (entry.ContainsKey(key)) return false;
            entry[key] = value?.DeepClone();
            Save(root);
            return true;
        }
    }

    public bool Contains(string tweakId)
    {
        lock (Gate) return Load()[tweakId] is JsonObject;
    }

    public void Remove(string tweakId)
    {
        lock (Gate)
        {
            var root = Load();
            if (root.Remove(tweakId)) Save(root);
        }
    }

    /// <summary>Removes one restored original; the tweak's entry goes when nothing is left in it.</summary>
    public void RemoveKey(string tweakId, string key)
    {
        lock (Gate)
        {
            var root = Load();
            if (root[tweakId] is not JsonObject entry || !entry.Remove(key)) return;
            if (entry.Count == 0) root.Remove(tweakId);
            Save(root);
        }
    }

    /// <summary>
    /// Fails closed: a damaged journal (empty, not an object, invalid JSON) is never replaced by a fresh one, because
    /// the originals in it would be lost. The previous version ("journal.json.bak") is used when it is intact.
    /// </summary>
    private JsonObject Load()
    {
        if (!File.Exists(FilePath)) return [];
        if (TryParse(FilePath) is { } root) return root;
        var bak = FilePath + ".bak";
        if (TryParse(bak) is { } previous)
        {
            // Keep the damaged file for a closer look; the next save replaces it with the recovered content.
            var damaged = FilePath + ".damaged";
            if (!File.Exists(damaged)) File.Copy(FilePath, damaged);
            Log.Warn($"Journal {FilePath} is damaged, using the previous version {bak}.");
            return previous;
        }
        throw new InvalidDataException(
            $"The backup of original values ({FilePath}) is damaged and there is no intact previous version. " +
            "Frametide does not overwrite it: tweaks cannot be applied or reverted until the file is repaired or removed " +
            "(removing it loses the saved original values).");
    }

    /// <summary>The file as a JSON object, or null when it is missing, empty or not a JSON object. Read errors are thrown.</summary>
    private static JsonObject? TryParse(string path)
    {
        try { return JsonFile.ReadNode(path) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private void Save(JsonObject root) => JsonFile.WriteNode(FilePath, root);
}
