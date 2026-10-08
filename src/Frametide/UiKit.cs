using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static Frametide.Localization.Loc;

namespace Frametide;

/// <summary>Small building blocks for rows, badges and buttons in the dark theme.</summary>
public static class UiKit
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>"l,t,r,b" -> Thickness. Invariant on purpose: with a German culture "0,0,0,4" would be read as a decimal number.</summary>
    public static Thickness Th(string margin) => (Thickness)new ThicknessConverter().ConvertFromInvariantString(margin)!;

    public static TextBlock Text(string text, double size = 13, string brush = "Text", bool bold = false, string margin = "0", bool translate = true) => new()
    {
        Text = translate ? T(text) : text, FontSize = size, Foreground = Brush(brush), TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = Th(margin),
    };

    public static Border Badge(string text, string fg, string bg) => new()
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center, Background = Brush(bg), Child = Text(text, 11, fg, bold: true),
    };

    public static Ellipse Dot(string brush) => new()
    {
        Width = 9, Height = 9, Fill = Brush(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 12, 0),
    };

    public static Button Button(string text, Action? onClick = null, string? style = null, string margin = "8,0,0,0")
    {
        var b = new Button { Content = T(text), Margin = Th(margin), VerticalAlignment = VerticalAlignment.Center };
        if (style is not null) b.Style = (Style)Application.Current.FindResource(style);
        if (onClick is not null) b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Row: [dot] [title + description] [right-hand elements].</summary>
    public static Grid Row(string? dot, string title, string? description, IEnumerable<UIElement> right, string margin = "0,0,0,12", bool translate = true, bool boldTitle = true)
    {
        var g = new Grid { Margin = Th(margin) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (dot is not null) g.Children.Add(Dot(dot));
        var text = new StackPanel();
        text.Children.Add(Text(title, 13.5, bold: boldTitle, translate: translate));
        if (!string.IsNullOrEmpty(description)) text.Children.Add(Text(description, 12, "Muted", margin: "0,2,16,0", translate: translate));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var rp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var r in right) rp.Children.Add(r);
        Grid.SetColumn(rp, 2);
        g.Children.Add(rp);
        return g;
    }

    public static Border Card(UIElement content) => new() { Style = (Style)Application.Current.FindResource("Card"), Child = content };

    public static MessageBoxResult Ask(string text, MessageBoxButton buttons = MessageBoxButton.YesNo, MessageBoxImage icon = MessageBoxImage.Question) =>
        MessageBox.Show(Application.Current.MainWindow, text, "Frametide", buttons, icon);

    public static void Info(string text, MessageBoxImage icon = MessageBoxImage.Information) =>
        MessageBox.Show(Application.Current.MainWindow, text, "Frametide", MessageBoxButton.OK, icon);
}
