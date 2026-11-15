using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameOptimizer.App;

/// <summary>Small helpers for windows built in code, in the app's dark theme.</summary>
public static class Ui
{
    public static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    public static TextBlock Text(string s, double size = 13, bool bold = false, string brush = "Text", double top = 0, bool mono = false, bool wrap = true) => new()
    {
        Text = s, FontSize = size, Margin = new Thickness(0, top, 0, 0), Foreground = B(brush),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
    };

    public static Button Button(string content, RoutedEventHandler click, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
        if (primary) b.Style = (Style)Application.Current.FindResource("Primary");
        b.Click += click;
        return b;
    }

    public static Border Card(UIElement child, double bottom = 8) => new()
    {
        Background = B("Panel"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
        Margin = new Thickness(0, 0, 0, bottom), Child = child,
    };

    public static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static StackPanel Stack(params UIElement[] items)
    {
        var p = new StackPanel();
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static ComboBox Combo(IEnumerable<string> items, int selected = 0, double width = 220)
    {
        var c = new ComboBox { Width = width, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var i in items) c.Items.Add(i);
        if (c.Items.Count > 0) c.SelectedIndex = Math.Clamp(selected, 0, c.Items.Count - 1);
        return c;
    }

    public static TextBox Box(string text = "", double width = 220) => new()
    {
        Text = text, Width = width, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 2, 4, 2),
        Background = B("PanelHi"), Foreground = B("Text"), BorderBrush = B("Line"), CaretBrush = B("Text"),
    };

    public static Window Window(string title, double w, double h, Window? owner) => new()
    {
        Title = title, Width = w, Height = h, MinWidth = 480, MinHeight = 320, Owner = owner,
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        Background = B("Bg"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
    };

    /// <summary>Window layout: scrolling content with a button bar at the bottom.</summary>
    public static DockPanel Frame(UIElement content, UIElement? footer)
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        if (footer is not null)
        {
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
        }
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return root;
    }

    public static StackPanel Footer(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static void Info(Window owner, string text, string title = "GameOptimizer") =>
        MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(Window owner, string text, string title) =>
        MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
}
