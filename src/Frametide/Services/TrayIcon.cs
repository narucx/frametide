using System.Windows;
using Frametide.Core.Boost;
using static Frametide.Localization.Loc;
using Forms = System.Windows.Forms;

namespace Frametide.Services;

/// <summary>Notification area icon with a small menu. The icon gets a green dot while Game Boost is active.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon = new();
    private readonly System.Drawing.Icon _idle = LoadIcon("frametide.ico");
    private readonly System.Drawing.Icon _active = LoadIcon("frametide-active.ico");
    private readonly Forms.ToolStripMenuItem _open = new();
    private readonly Forms.ToolStripMenuItem _boost = new();
    private readonly Forms.ToolStripMenuItem _auto = new();
    private readonly Forms.ToolStripMenuItem _exit = new();

    public TrayIcon(App app)
    {
        _open.Font = new System.Drawing.Font(_open.Font, System.Drawing.FontStyle.Bold);
        _open.Click += (_, _) => app.ShowMain();
        _boost.Click += async (_, _) => await BoostRunner.ToggleAsync();
        _auto.Click += async (_, _) => { var on = !_auto.Checked; await BoostRunner.RunAsync(() => GameBoost.SetAutoBoost(on)); };
        _exit.Click += (_, _) => app.ExitApp();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([_open, new Forms.ToolStripSeparator(), _boost, _auto, new Forms.ToolStripSeparator(), _exit]);
        _icon.ContextMenuStrip = menu;
        _icon.MouseDoubleClick += (_, _) => app.ShowMain();
        _icon.Icon = _idle;
        _icon.Text = "Frametide";
        UpdateTexts();
        _icon.Visible = true;
    }

    public void UpdateTexts()
    {
        _open.Text = T("Open Frametide");
        _auto.Text = T("Auto Game Boost");
        _exit.Text = T("Exit Frametide");
    }

    public void Update(BoostState? state, bool auto)
    {
        _icon.Icon = state is null ? _idle : _active;
        _boost.Text = state is null ? T("Start Game Boost") : T("Stop Game Boost");
        _auto.Checked = auto;
        var text = "Frametide - " + (state is null ? (auto ? T("waiting for a game") : T("Game Boost off"))
            : state.Trigger is { } games ? T("Game Boost active ({0})", games) : T("Game Boost active"));
        _icon.Text = text.Length > 63 ? text[..60] + "..." : text;   // Windows limit
    }

    public void Tip(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private static System.Drawing.Icon LoadIcon(string file)
    {
        using var s = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{file}"))!.Stream;
        return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _idle.Dispose();
        _active.Dispose();
    }
}
