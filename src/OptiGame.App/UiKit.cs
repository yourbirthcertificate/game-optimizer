using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OptiGame.App;

/// <summary>
/// Helpers for the windows that are built in code (preview, history, profiles...). Every colour is a Windows 11
/// Fluent theme brush, so these windows follow light and dark like the rest of the app. Shared XAML styles live in
/// Themes/Styles.xaml; this file only maps the old role names ("Muted", "Warn"...) to theme brushes.
/// </summary>
public static class Ui
{
    /// <summary>Role names used throughout the dialogs, mapped to Fluent theme resources.</summary>
    static string Key(string role) => role switch
    {
        "Text" => "TextFillColorPrimaryBrush",
        "Muted" => "TextFillColorSecondaryBrush",
        "Warn" => "SystemFillColorCautionBrush",
        "Accent" or "Custom" => "AccentTextFillColorPrimaryBrush",
        "Panel" => "CardBackgroundFillColorDefaultBrush",
        "PanelHi" => "ControlFillColorSecondaryBrush",
        "Line" => "CardStrokeColorDefaultBrush",
        _ => role,
    };

    public static Brush B(string role) => (Brush)Application.Current.FindResource(Key(role));

    public static TextBlock Text(string s, double size = 13, bool bold = false, string brush = "Text", double top = 0, bool mono = false, bool wrap = true)
    {
        var t = new TextBlock
        {
            Text = s, FontSize = size, Margin = new Thickness(0, top, 0, 0),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, Key(brush));
        if (mono) t.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        return t;
    }

    public static Button Button(string content, RoutedEventHandler click, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
        if (primary) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        b.Click += click;
        return b;
    }

    /// <summary>A Fluent card. Background and stroke follow the theme.</summary>
    public static Border Card(UIElement child, double bottom = 8)
    {
        var b = new Border { Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, bottom), Child = child };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        return b;
    }

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
        Text = text, Width = width, Margin = new Thickness(0, 0, 8, 0),
    };

    /// <summary>A dialog window with the same chrome as the main window (native title bar, Mica, dark when Windows is dark).</summary>
    public static Window Window(string title, double w, double h, Window? owner)
    {
        var win = new Window
        {
            Title = title, Width = w, Height = h, MinWidth = 480, MinHeight = 320, Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13,
        };
        Style(win);
        return win;
    }

    /// <summary>Applies the shared dialog look to a window class that derives from Window.</summary>
    public static void Style(Window win)
    {
        Chrome.Attach(win);
        win.UseLayoutRounding = true;
        win.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
    }

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

    public static void Info(Window owner, string text, string title = "OptiGame+") =>
        MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(Window owner, string text, string title) =>
        MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
}
