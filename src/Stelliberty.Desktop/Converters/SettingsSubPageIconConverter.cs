using System.Globalization;
using Avalonia.Data.Converters;
using IconPacks.Avalonia.MingCuteIcons;
using Stelliberty.Presentation.ViewModels;

namespace Stelliberty.Desktop.Converters;

/// <summary>把设置子页面映射成面包屑图标，与设置列表里的图标保持同一套。</summary>
internal sealed class SettingsSubPageIconConverter : IValueConverter
{
    public static readonly SettingsSubPageIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SettingsSubPage.Theme => PackIconMingCuteIconsKind.Palette3Line,
        SettingsSubPage.Language => PackIconMingCuteIconsKind.Translate2Line,
        SettingsSubPage.ClashFeatures => PackIconMingCuteIconsKind.SendPlaneLine,
        SettingsSubPage.AppBehavior => PackIconMingCuteIconsKind.ToggleLeftLine,
        SettingsSubPage.DataManagement => PackIconMingCuteIconsKind.StorageLine,
        SettingsSubPage.Update => PackIconMingCuteIconsKind.Refresh1Line,
        SettingsSubPage.About => PackIconMingCuteIconsKind.InformationLine,
        SettingsSubPage.AppLog => PackIconMingCuteIconsKind.FileLine,
        SettingsSubPage.Network => PackIconMingCuteIconsKind.RouteLine,
        SettingsSubPage.PortControl => PackIconMingCuteIconsKind.Settings6Line,
        SettingsSubPage.SystemIntegration => PackIconMingCuteIconsKind.ComputerLine,
        SettingsSubPage.Dns => PackIconMingCuteIconsKind.EarthLine,
        SettingsSubPage.Performance => PackIconMingCuteIconsKind.PerformanceLine,
        SettingsSubPage.CoreLog => PackIconMingCuteIconsKind.TerminalBoxLine,
        _ => PackIconMingCuteIconsKind.Settings3Line
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
