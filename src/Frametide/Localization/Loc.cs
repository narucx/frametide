using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Frametide.Localization;

/// <summary>
/// Translations: source texts are English and are the keys; Lang/(code).json maps them to the translation.
/// Missing entries fall back to English.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public static string Code { get; private set; } = "en";

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = [("en", "English"), ("de", "Deutsch")];

    public static void Load(string code)
    {
        Code = code;
        _map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code == "en") return;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Frametide.Lang.{code}.json");
        if (stream is null) { Code = "en"; return; }
        // Case-sensitive on purpose: "POWER" and "Power" are different keys.
        _map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    /// <summary>Translates a text; with arguments it is a format string ({0}, {1}, ...).</summary>
    public static string T(string text, params object?[] args)
    {
        var s = _map.TryGetValue(text, out var t) && !string.IsNullOrEmpty(t) ? t : text;
        return args.Length == 0 ? s : string.Format(s, args);
    }

    /// <summary>Translates the static texts of a XAML tree once after loading.</summary>
    public static void TranslateTree(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock tb when !string.IsNullOrEmpty(tb.Text) && tb.Inlines.Count <= 1: tb.Text = T(tb.Text); break;
            case ContentControl cc when cc.Content is string s: cc.Content = T(s); break;
        }
        if (node is FrameworkElement { ToolTip: string tip } fe) fe.ToolTip = T(tip);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) TranslateTree(child);
    }
}
