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
        if (ReadText(path) is not { } text || string.IsNullOrWhiteSpace(text)) return null;
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

    /// <summary>
    /// Readers share delete and write access, so a writer can replace or delete the file while another thread reads it.
    /// A short sharing violation (e.g. a virus scanner) is retried. Null when the file does not exist.
    /// </summary>
    private static string? ReadText(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(20); }
        }
    }

    private static void WriteText(string path, string text)
    {
        var tmp = path + ".tmp";
        // Flushed to the disk before the replace: after a power loss the file is either the old or the new one, never empty.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
