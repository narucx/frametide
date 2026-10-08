using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Frametide.Core.Windows;

/// <summary>A registry value as stored in the journal: whether it existed, its kind and its value.</summary>
public sealed record RegValue(bool Exists, RegistryValueKind Kind, object? Value)
{
    public static readonly RegValue Missing = new(false, RegistryValueKind.Unknown, null);

    /// <summary>Journal format: {"Exists": bool, "Type": "DWord", "Value": ...}.</summary>
    public JsonObject ToJson() => new()
    {
        ["Exists"] = Exists,
        ["Type"] = Exists ? Kind.ToString() : null,
        ["Value"] = Exists ? Reg.ToJson(Kind, Value) : null,
    };

    public static RegValue FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || o["Exists"]?.GetValue<bool>() != true) return Missing;
        var kind = Enum.TryParse<RegistryValueKind>((string?)o["Type"], ignoreCase: true, out var k) ? k : RegistryValueKind.String;
        return new RegValue(true, kind, Reg.FromJson(kind, o["Value"]));
    }
}

/// <summary>
/// Registry access with drive-style paths ("HKLM:\SOFTWARE\..."), because tweak definitions and the journal
/// use them. Always the 64-bit view.
/// </summary>
public static class Reg
{
    public static (RegistryKey Root, string SubKey) Parse(string path)
    {
        var p = path.Replace('/', '\\');
        var sep = p.IndexOf('\\');
        var hive = (sep < 0 ? p : p[..sep]).TrimEnd(':').ToUpperInvariant();
        var sub = sep < 0 ? "" : p[(sep + 1)..].Trim('\\');
        var root = hive switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => throw new ArgumentException($"Unknown registry hive in '{path}'"),
        };
        return (RegistryKey.OpenBaseKey(root, RegistryView.Registry64), sub);
    }

    public static RegValue Get(string path, string name)
    {
        var (root, sub) = Parse(path);
        using (root)
        using (var key = root.OpenSubKey(sub))
        {
            if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return RegValue.Missing;
            return new RegValue(true, key.GetValueKind(name), key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
    }

    public static void Set(string path, string name, RegistryValueKind kind, object value)
    {
        var (root, sub) = Parse(path);
        using (root)
        using (var key = root.CreateSubKey(sub, writable: true))
            key.SetValue(name, Coerce(kind, value), kind);
    }

    public static void Remove(string path, string name)
    {
        var (root, sub) = Parse(path);
        using (root)
        using (var key = root.OpenSubKey(sub, writable: true))
            key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public static bool Equals(string path, string name, RegistryValueKind kind, object expected)
    {
        var cur = Get(path, name);
        if (!cur.Exists || cur.Value is null) return false;
        return kind switch
        {
            // The registry returns DWORDs as int (0xFFFFFFFF -> -1): compare the 32-bit pattern.
            RegistryValueKind.DWord => (Convert.ToInt64(cur.Value) & 0xFFFFFFFF) == (Convert.ToInt64(expected) & 0xFFFFFFFF),
            RegistryValueKind.QWord => Convert.ToInt64(cur.Value) == Convert.ToInt64(expected),
            RegistryValueKind.Binary => cur.Value is byte[] a && Coerce(kind, expected) is byte[] b && a.SequenceEqual(b),
            _ => string.Equals(cur.Value.ToString(), expected.ToString(), StringComparison.Ordinal),
        };
    }

    public static bool KeyExists(string path)
    {
        var (root, sub) = Parse(path);
        using (root)
        using (var key = root.OpenSubKey(sub))
            return key is not null;
    }

    public static void DeleteKeyTree(string path)
    {
        var (root, sub) = Parse(path);
        using (root)
            root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
    }

    /// <summary>Deletes a key only when it has no values and no subkeys left.</summary>
    public static void DeleteKeyIfEmpty(string path)
    {
        var (root, sub) = Parse(path);
        using (root)
        {
            using (var key = root.OpenSubKey(sub))
                if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
            root.DeleteSubKey(sub, throwOnMissingSubKey: false);
        }
    }

    /// <summary>Writes a value back as it was, or removes it when it did not exist.</summary>
    public static void Restore(string path, string name, RegValue original)
    {
        if (original.Exists && original.Value is not null) Set(path, name, original.Kind, original.Value);
        else Remove(path, name);
    }

    internal static object Coerce(RegistryValueKind kind, object value) => kind switch
    {
        // DWORD values above int.MaxValue (e.g. 0xFFFFFFFF) must be passed as their signed bit pattern.
        RegistryValueKind.DWord => unchecked((int)Convert.ToUInt32(Convert.ToInt64(value) & 0xFFFFFFFF)),
        RegistryValueKind.QWord => Convert.ToInt64(value),
        RegistryValueKind.Binary => value as byte[] ?? ((IEnumerable<object>)value).Select(Convert.ToByte).ToArray(),
        RegistryValueKind.MultiString => value as string[] ?? ((IEnumerable<object>)value).Select(x => x.ToString() ?? "").ToArray(),
        _ => value.ToString() ?? "",
    };

    internal static JsonNode? ToJson(RegistryValueKind kind, object? value) => value switch
    {
        null => null,
        byte[] bytes => new JsonArray(bytes.Select(b => (JsonNode?)JsonValue.Create((int)b)).ToArray()),
        string[] lines => new JsonArray(lines.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        int i when kind == RegistryValueKind.DWord => JsonValue.Create((long)unchecked((uint)i)),
        long l => JsonValue.Create(l),
        _ => JsonValue.Create(value.ToString()),
    };

    internal static object? FromJson(RegistryValueKind kind, JsonNode? node)
    {
        if (node is null) return null;
        return kind switch
        {
            RegistryValueKind.DWord or RegistryValueKind.QWord => node.GetValue<long>(),
            RegistryValueKind.Binary => node.AsArray().Select(n => (byte)n!.GetValue<int>()).ToArray(),
            RegistryValueKind.MultiString => node is JsonArray arr ? arr.Select(n => (string?)n ?? "").ToArray() : [(string?)node ?? ""],
            _ => node.ToString(),
        };
    }
}
