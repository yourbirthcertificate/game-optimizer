using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OptiGame.App.ViewModels;

namespace OptiGame.App;

/// <summary>
/// "An update is available": the new version, what changed (plain text from the release notes), a progress bar, and one
/// main button that downloads, verifies, then restarts to install. Remind me later and Skip this version stay out of the way.
/// </summary>
public sealed class UpdateWindow : Window
{
    readonly AppVm _vm;
    readonly Action _exitApp;
    readonly TextBlock _status = Ui.Text("", 12, brush: "Muted", top: 8);
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 4, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    readonly Button _main;
    readonly Button _later;
    readonly Button _skip;

    public UpdateWindow(AppVm vm, Action exitApp, Window owner)
    {
        _vm = vm; _exitApp = exitApp;
        Title = "OptiGame+ update"; Width = 560; Height = 520; MinWidth = 460; MinHeight = 380;
        Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13;
        Ui.Style(this);

        _main = Ui.Button("", async (_, _) => await _vm.UpdateStepAsync(_exitApp), primary: true);
        _later = Ui.Button("Remind me later", (_, _) => { _vm.RemindLater(); Close(); });
        _skip = Ui.Button("Skip this version", (_, _) => { _vm.SkipThisVersion(); Close(); }, tip: "Stay quiet until a newer version appears");
        _main.IsDefault = true;

        var notes = new TextBlock { Text = vm.UpdateNotes, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        notes.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var notesBox = Ui.Card(new ScrollViewer { Content = notes, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var body = Ui.Stack(
            Ui.Text($"OptiGame+ {vm.UpdateVersionText} is available", 20, bold: true),
            Ui.Text($"You have {Core.Updates.UpdateService.Current}. Your settings, backups and history are kept.", 12, brush: "Muted", top: 2),
            Ui.Text("What is new", 13, bold: true, top: 14),
            notesBox, _bar, _status);
        var footer = Ui.Footer(_skip, _later, _main);
        footer.Children.OfType<Button>().Last().Margin = new Thickness(0);
        Content = Ui.Frame(body, footer);

        vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmChanged;
        Refresh();
    }

    void OnVmChanged(object? s, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    void Refresh()
    {
        _main.Content = _vm.UpdateActionText;
        _main.IsEnabled = _vm.Phase != AppVm.UpdatePhase.Downloading;
        _later.IsEnabled = _skip.IsEnabled = _vm.Phase != AppVm.UpdatePhase.Downloading;
        _bar.Visibility = _vm.Phase is AppVm.UpdatePhase.Downloading or AppVm.UpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        _bar.Value = _vm.UpdateProgress;
        _status.Text = _vm.UpdateStatus;
    }
}
