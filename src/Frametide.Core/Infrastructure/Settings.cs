using System.Text.Json.Nodes;

namespace Frametide.Core.Infrastructure;

/// <summary>
/// config.json as a JSON object, so keys this version does not know survive every save (forward compatible).
/// </summary>
public static class Settings
{
    private static readonly object Gate = new();

    public static string Language
    {
        get => GetString("Language", "en");
        set => Set("Language", value);
    }

    public static string GetString(string key, string fallback)
    {
        lock (Gate) return (string?)Load()[key] ?? fallback;
    }

    public static bool GetBool(string key, bool fallback)
    {
        lock (Gate) return Load()[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    }

    public static JsonNode? GetNode(string key)
    {
        lock (Gate) return Load()[key]?.DeepClone();
    }

    public static void Set(string key, JsonNode? value)
    {
        lock (Gate)
        {
            var root = Load();
            root[key] = value?.DeepClone();
            JsonFile.WriteNode(AppPaths.Config, root);
        }
    }

    private static JsonObject Load() => JsonFile.ReadNode(AppPaths.Config) as JsonObject ?? [];
}
