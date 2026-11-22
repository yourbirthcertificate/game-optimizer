using System.Windows;
using System.Windows.Controls;
using OptiGame.App.ViewModels;
using OptiGame.Core.Live;

namespace OptiGame.App.Pages;

public partial class AppsPage : UserControl
{
    readonly AppVm _vm;

    public AppsPage(AppVm vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    async void CloseSelected_Click(object sender, RoutedEventArgs e)
    {
        // Closing is always the user's click; apps that may hold unsaved work ask first and are never pre-ticked.
        var review = _vm.SelectedReviewNames();
        if (review.Length > 0)
        {
            var r = MessageBox.Show(Window.GetWindow(this),
                $"{string.Join(", ", review)} may have unsaved work. OptiGame+ will ask the app to close, and it will normally prompt you to save first.\n\nClose anyway?",
                "Close apps that may have unsaved work?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
        }
        await _vm.CloseSelectedAsync();
    }

    void SelectRecommended_Click(object sender, RoutedEventArgs e) => _vm.SelectRecommended();
    void SelectNone_Click(object sender, RoutedEventArgs e) => _vm.SelectNone();
    void KeepApp_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is AppGroup a) _vm.KeepApp(a); }
}
