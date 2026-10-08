using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Frametide.Core.Infrastructure;

/// <summary>JSON files written atomically (temp file + replace), so a crash never leaves a half-written file.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep umlauts and quotes readable
        PropertyNameCaseInsensitive = true,
    };

    public static JsonNode? ReadNode(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Files written by other tools may start with a UTF-8 BOM.
        return JsonNode.Parse(text.TrimStart('\uFEFF'), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
    }

    public static T? Read<T>(string path)
    {
        var node = ReadNode(path);
        return node is null ? default : node.Deserialize<T>(Options);
    }

    public static void WriteNode(string path, JsonNode? node) => WriteText(path, node?.ToJsonString(Options) ?? "null");

    public static void Write<T>(string path, T value) => WriteText(path, JsonSerializer.Serialize(value, Options));

    private static void WriteText(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
