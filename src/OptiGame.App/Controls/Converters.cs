using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OptiGame.App.Controls;

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InvertBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InvertBool : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is bool b ? !b : v;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is bool b ? !b : v;
}
