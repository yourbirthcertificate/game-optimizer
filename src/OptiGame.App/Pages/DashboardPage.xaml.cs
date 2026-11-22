using System.Windows;
using System.Windows.Controls;
using OptiGame.App.ViewModels;
using OptiGame.Core.Live;

namespace OptiGame.App.Pages;

public partial class DashboardPage : UserControl
{
    readonly AppVm _vm;

    public DashboardPage(AppVm vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    async void Boost_Click(object sender, RoutedEventArgs e) => await _vm.BoostOrRestoreAsync();
    void TipAction_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as Tip)?.Action?.Invoke();
    async void OpenVendor_Click(object sender, RoutedEventArgs e) => await _vm.OpenVendorAsync();
}
