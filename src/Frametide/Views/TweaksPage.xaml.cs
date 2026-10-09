using System.Windows;
using System.Windows.Controls;
using Frametide.Core.Infrastructure;
using Frametide.Core.Tweaks;
using Frametide.Core.Windows;
using static Frametide.Localization.Loc;
using static Frametide.UiKit;

namespace Frametide.Views;

public partial class TweaksPage : UserControl
{
    private readonly TweakEngine _engine = TweakEngine.Default;
    private readonly MainWindow _main;

    /// <summary>Tweaks with a backup of their originals (only those get a Revert button).</summary>
    private IReadOnlySet<string> _revertable = new HashSet<string>();

    /// <summary>Status per tweak after the last check; the overview reads the summary from here.</summary>
    public IReadOnlyDictionary<string, TweakStatus> Status { get; private set; } = new Dictionary<string, TweakStatus>();
    public string SummaryText { get; private set; } = "";
    public int RepairCount { get; private set; }
    public event Action? Checked;

    public TweaksPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        TranslateTree(this);
        foreach (var p in TweakProfile.All) ProfileCombo.Items.Add(new ComboBoxItem { Content = T(p.Name), Tag = p });
        ProfileCombo.SelectionChanged += (_, _) => ProfileDesc.Text = ProfileCombo.SelectedItem is ComboBoxItem { Tag: TweakProfile p } ? T(p.Description) : "";
        ProfileCombo.SelectedIndex = 0;
        ProfileApply.Click += (_, _) => { if (ProfileCombo.SelectedItem is ComboBoxItem { Tag: TweakProfile p }) ApplyProfileWithPrompt(p); };
        Refresh.Click += async (_, _) => await CheckAsync();
        RevertAll.Click += async (_, _) =>
        {
            if (Ask(T("Revert all tweaks set by Frametide to the backed-up original state?"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _main.RunAsync("Reverting everything", _engine.RevertAll);
            await CheckAsync();
            Info(T("Reverted. Some changes only take effect after a reboot/sign-out."));
        };
        RepairAll.Click += async (_, _) => await RepairAsync(_engine.FindRepairs().Select(f => f.Repair).ToList());
    }

    public async Task CheckAsync()
    {
        string? journalError = null;
        IReadOnlySet<string> Revertable()
        {
            try { return _engine.RevertableIds(); }
            catch (System.IO.InvalidDataException e) { journalError = e.Message; return new HashSet<string>(); }
        }
        var (status, findings, revertable) = await _main.RunAsync("Checking tweaks", () =>
            (_engine.Tweaks.ToDictionary(t => t.Id, _engine.GetStatus), _engine.FindRepairs(), Revertable()));
        Status = status;
        _revertable = revertable;
        Show(findings);
        Checked?.Invoke();
        if (journalError is not null)
        {
            Log.Error(journalError);
            Info(T("The backup of original values is damaged. Tweaks cannot be applied or reverted until it is repaired. Details are in the log."), MessageBoxImage.Error);
        }
    }

    private void Show(IReadOnlyList<RepairFinding> findings)
    {
        TweakGroups.Children.Clear();
        foreach (var group in _engine.Tweaks.GroupBy(t => t.Category))
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(group.Key, 16, bold: true, margin: "0,0,0,14"));
            foreach (var t in group) panel.Children.Add(TweakRow(t, Status[t.Id]));
            TweakGroups.Children.Add(Card(panel));
        }

        // Tweaks that do not apply to this PC are not counted.
        var available = _engine.Tweaks.Where(t => Status[t.Id] != TweakStatus.NotAvailable).ToList();
        var recommended = available.Where(t => t.Recommended).ToList();
        SummaryText = T("{0} of {1} recommended tweaks active ({2} of {3} in total).",
            recommended.Count(t => Status[t.Id] == TweakStatus.Applied), recommended.Count,
            available.Count(t => Status[t.Id] == TweakStatus.Applied), available.Count);
        Summary.Text = SummaryText;

        RepairList.Children.Clear();
        RepairCount = findings.Count;
        RepairAll.IsEnabled = findings.Count > 0;
        if (findings.Count == 0) RepairList.Children.Add(Row("Good", "No harmful tweaks found.", null, [], "0"));
        foreach (var f in findings)
        {
            var fix = Button("Repair", async () => await RepairAsync([f.Repair]));
            RepairList.Children.Add(Row("Warn", $"{T(f.Repair.Name)}: {f.Message}", T(f.Repair.Description), [fix], translate: false));
        }
    }

    private Grid TweakRow(Tweak t, TweakStatus st)
    {
        var right = new List<UIElement>();
        if (t.Recommended) right.Add(Badge("Recommended", "Good", "GoodSoft"));
        if (t.Risk == Risk.Moderate) right.Add(Badge("Use with care", "Warn", "WarnSoft"));
        if (t.Risk == Risk.Risky) right.Add(Badge("Risky", "Bad", "BadSoft"));
        if (t.Restart == RestartNeed.Reboot) right.Add(Badge("Reboot", "Muted", "Panel3"));
        if (t.Restart == RestartNeed.SignOut) right.Add(Badge("Sign out", "Muted", "Panel3"));
        var canRevert = _revertable.Contains(t.Id);
        Button button = st switch
        {
            TweakStatus.NotAvailable => Button("Not available"),
            TweakStatus.Applied or TweakStatus.Partial when canRevert => Button("Revert", async () =>
            {
                await _main.RunAsync("Tweak", () => _engine.Revert(t));
                await CheckAsync();
            }),
            // Already set before Frametide (or by another tool): there is no original to go back to.
            TweakStatus.Applied => Button("No backup"),
            _ => Button("Apply", async () =>
            {
                var r = await _main.RunAsync("Tweak", () => _engine.Apply(t));
                await CheckAsync();
                if (!r.Success && r.Hint is not null) Info(T(r.Hint), MessageBoxImage.Warning);
            }, "Primary"),
        };
        button.IsEnabled = st != TweakStatus.NotAvailable && (st != TweakStatus.Applied || canRevert);
        if (st == TweakStatus.Applied && !canRevert)
        {
            button.ToolTip = T("This setting was already active before Frametide changed anything, so there is no original value to restore.");
            ToolTipService.SetShowOnDisabled(button, true);
        }
        button.Margin = new Thickness(12, 0, 0, 0);
        button.MinWidth = 120;
        right.Add(button);
        var dot = st switch
        {
            TweakStatus.Applied => "Good", TweakStatus.Partial => "Warn", TweakStatus.Error => "Bad", TweakStatus.NotAvailable => "Line", _ => "Muted",
        };
        return Row(dot, t.Name, t.Description, right);
    }

    public async void ApplyProfileWithPrompt(TweakProfile p)
    {
        var ans = Ask(T("Apply profile '{0}' ({1} tweaks)?\n\nCreate a restore point first? (recommended)", T(p.Name), p.TweakIds.Count), MessageBoxButton.YesNoCancel);
        if (ans == MessageBoxResult.Cancel) return;
        await _main.RunAsync("Applying profile", () =>
        {
            if (ans == MessageBoxResult.Yes) RestorePoint.Create($"Frametide before profile {p.Name}");
            _engine.ApplyProfile(p);
        });
        await CheckAsync();
        Info(T("Profile applied. Tweaks marked \"Reboot\"/\"Sign out\" only take effect after that."));
    }

    private async Task RepairAsync(IReadOnlyList<Repair> repairs)
    {
        if (repairs.Count == 0) return;
        await _main.RunAsync("Repair", () => { foreach (var r in repairs) _engine.Fix(r); });
        await CheckAsync();
        Info(T("Repair finished. Some changes (BCD, HPET) only take effect after a reboot."));
    }
}
