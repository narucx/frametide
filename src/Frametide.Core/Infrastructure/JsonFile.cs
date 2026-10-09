using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Frametide.Core.Infrastructure;

/// <summary>JSON files written atomically and durably (temp file, flush, replace), so a crash never leaves a half-written file.</summary>
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

    /// <summary>Deletes the file and the backup copy that <see cref="Write{T}"/> keeps next to it.</summary>
    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".bak");
    }

    /// <summary>
    /// Writes to a unique temp file (the app and the "--apply-gpu-profile" process may write the same file), flushes it
    /// to disk and only then swaps it in, so a power loss leaves either the old or the new file. The previous version
    /// is kept as "(file).bak".
    /// </summary>
    private static void WriteText(string path, string text)
    {
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(new UTF8Encoding(false).GetBytes(text));
                fs.Flush(flushToDisk: true);
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
                    else File.Move(tmp, path);
                    return;
                }
                // Another process is reading or replacing the file at this moment: try again shortly.
                catch (IOException) when (attempt < 5) { Thread.Sleep(50 * attempt); }
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
