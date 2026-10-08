using System.Text.Json.Nodes;

namespace Frametide.Core.Infrastructure;

/// <summary>
/// Backup of original values: { "tweak id": { "key": original value } }. Before a tweak changes anything it saves
/// the original here, and only the first time, so applying twice never stores an already-changed value.
/// "Revert" restores exactly these values.
/// Key conventions: "reg|HKLM:\path|name" -> {Exists, Type, Value}; "svc|name" -> start mode;
/// power plan GUID -> {AC, DC}; "pnp|class key" -> {Exists, Value}.
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

    public void Remove(string tweakId)
    {
        lock (Gate)
        {
            var root = Load();
            if (root.Remove(tweakId)) Save(root);
        }
    }

    private JsonObject Load() => JsonFile.ReadNode(FilePath) as JsonObject ?? [];

    private void Save(JsonObject root) => JsonFile.WriteNode(FilePath, root);
}
