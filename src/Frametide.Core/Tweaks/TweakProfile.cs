namespace Frametide.Core.Tweaks;

public sealed record TweakProfile(string Name, string Description, IReadOnlyList<string> TweakIds)
{
    private static readonly string[] Gaming =
        ["gaming.gamedvr", "gaming.gamemode", "gaming.gamebar_tips", "gaming.hags", "gaming.windowed_opt", "gaming.mouse_accel", "gaming.sticky_keys"];
    private static readonly string[] Privacy =
        ["privacy.telemetry", "privacy.adid", "privacy.activity", "privacy.feedback", "privacy.websearch"];
    private static readonly string[] AdsAi =
        ["ads.suggestions", "ads.start_reco", "ai.copilot", "ai.recall", "ads.widgets"];

    public static readonly TweakProfile Recommended = new("Recommended",
        "Everything that measurably helps or only annoys when it is on. No risky tweaks, everything can be reverted.",
        [.. Gaming, "power.usb_suspend", "power.fast_startup", "net.eee", "net.nic_power", .. Privacy, .. AdsAi, "ads.edge", "ui.file_ext", "ui.end_task"]);

    public static readonly TweakProfile Minimal = new("Minimal",
        "Only gaming basics and ads/AI off. Nothing on network or services.",
        ["gaming.gamedvr", "gaming.gamemode", "gaming.gamebar_tips", "gaming.windowed_opt", "gaming.sticky_keys", .. AdsAi]);

    public static readonly TweakProfile Competitive = new("Competitive",
        "Recommended plus network fine-tuning (interrupt moderation/flow control off) and background apps off. Small effect, side effects possible.",
        [.. Gaming, "power.usb_suspend", "power.fast_startup", "net.eee", "net.nic_power", "net.interrupt_moderation", "net.flow_control",
         .. Privacy, "privacy.background_apps", .. AdsAi, "ads.edge", "ui.file_ext", "ui.end_task"]);

    public static readonly IReadOnlyList<TweakProfile> All = [Recommended, Minimal, Competitive];
}
