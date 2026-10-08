namespace Frametide.Core.Infrastructure;

/// <summary>
/// User-facing texts produced in Core (status of long-running tasks such as the undervolt). The app plugs in its
/// translation; without it the English text is used. Log lines stay English and do not go through here.
/// </summary>
public static class L
{
    public static Func<string, object?[], string> Translator { get; set; } = (text, args) => args.Length == 0 ? text : string.Format(text, args);

    public static string T(string text, params object?[] args) => Translator(text, args);
}
