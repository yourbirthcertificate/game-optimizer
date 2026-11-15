using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Optimize;

namespace GameOptimizer.App;

/// <summary>Lists every change, grouped by game, before anything is written.</summary>
public sealed class PreviewWindow : Window
{
    public PreviewWindow(IReadOnlyList<GamePlan> plans)
    {
        Title = "Preview changes";
        Width = 820; Height = 640; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Bg");
        FontFamily = new FontFamily("Segoe UI");

        var list = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        int ready = 0, changes = 0;
        foreach (var p in plans)
        {
            var box = new StackPanel();
            box.Children.Add(Text($"{p.Game.Name}  ·  {p.Step.Label()}  ·  {p.StatusText}", 14, bold: true,
                p.Status == PlanStatus.Running ? "Warn" : "Text"));
            foreach (var f in p.Files)
            {
                box.Children.Add(Text(f.Path, 11, false, "Muted", top: 6));
                foreach (var c in f.Changes) box.Children.Add(Text("    " + c, 12, false, "Text", mono: true));
            }
            if (p.Registry.Count > 0)
            {
                box.Children.Add(Text(@"HKEY_CURRENT_USER\" + p.Registry[0].SubKey, 11, false, "Muted", top: 6));
                foreach (var r in p.Registry) box.Children.Add(Text("    " + r.Display, 12, false, "Text", mono: true));
            }
            foreach (var s in p.Skipped) box.Children.Add(Text("Left alone: " + s, 12, false, "Muted", top: 2));
            foreach (var w in p.Warnings) box.Children.Add(Text("Warning: " + w, 12, false, "Warn", top: 2));
            list.Children.Add(new Border
            {
                Background = (Brush)Application.Current.FindResource("Panel"),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
                Child = box,
            });
            if (p.Status == PlanStatus.Ready) { ready++; changes += p.ChangeCount; }
        }

        var apply = new Button
        {
            Content = ready == 0 ? "Nothing to apply" : $"Apply {changes} change(s) to {ready} game(s)",
            Style = (Style)Application.Current.FindResource("Primary"),
            IsEnabled = ready > 0, IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(18, 8, 18, 8),
        };
        apply.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var note = Text("Every file and registry value is backed up first. Running games are skipped.", 12, false, "Muted");
        note.VerticalAlignment = VerticalAlignment.Center;

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(note);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    static TextBlock Text(string s, double size, bool bold, string brush, double top = 0, bool mono = false) => new()
    {
        Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = (Brush)Application.Current.FindResource(brush),
        FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
    };
}
