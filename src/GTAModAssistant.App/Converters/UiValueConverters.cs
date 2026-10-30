using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GTAModAssistant.Core.Models;
using GTAModAssistant.ViewModels;

namespace GTAModAssistant.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasVal = value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        if (Invert)
        {
            hasVal = !hasVal;
        }
        return hasVal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StatusSeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush InfoBrush = CreateFrozen("#1E3A5F");
    private static readonly SolidColorBrush VerifiedBrush = CreateFrozen("#064E3B");
    private static readonly SolidColorBrush WarningBrush = CreateFrozen("#78350F");
    private static readonly SolidColorBrush ErrorBrush = CreateFrozen("#7F1D1D");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            StatusBannerSeverity.Verified => VerifiedBrush,
            StatusBannerSeverity.Warning => WarningBrush,
            StatusBannerSeverity.Error => ErrorBrush,
            CompatibilitySeverity.Compatible => VerifiedBrush,
            CompatibilitySeverity.Advisory => InfoBrush,
            CompatibilitySeverity.WarningOutdated => WarningBrush,
            CompatibilitySeverity.IncompatibleBlocked => ErrorBrush,
            InstallVerificationState.VerifiedComplete => VerifiedBrush,
            InstallVerificationState.PendingOpenIvCommit => WarningBrush,
            InstallVerificationState.FailedVerification => ErrorBrush,
            AuditLogLevel.Verify => VerifiedBrush,
            AuditLogLevel.Warning => WarningBrush,
            AuditLogLevel.Error => ErrorBrush,
            AuditLogLevel.Security => ErrorBrush,
            _ => InfoBrush
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush CreateFrozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public sealed class EnabledBoolToStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "ENABLED" : "DISABLED (STASHED)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EnabledBoolToToggleActionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Disable" : "Enable";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
