using System.Windows;
using System.Windows.Controls;

namespace OptiGame.App.Controls;

public partial class SettingRow : UserControl
{
    public SettingRow() { InitializeComponent(); }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(SettingRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SwitchEnabledProperty = DependencyProperty.Register(nameof(SwitchEnabled), typeof(bool), typeof(SettingRow), new PropertyMetadata(true));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public bool SwitchEnabled { get => (bool)GetValue(SwitchEnabledProperty); set => SetValue(SwitchEnabledProperty, value); }
    public string OnOffText => IsOn ? "On" : "Off";
}
