using Avalonia;
using Avalonia.Automation;

namespace GhostShell.App;

public sealed partial class App
{
    private static bool _macOsLiveRegionSafetyInstalled;

    private static void InstallMacOsLiveRegionSafety()
    {
        if (!OperatingSystem.IsMacOS() || _macOsLiveRegionSafetyInstalled)
        {
            return;
        }

        _macOsLiveRegionSafetyInstalled = true;
        AutomationProperties.LiveSettingProperty.Changed
            .AddClassHandler<StyledElement>(OnMacOsLiveSettingChanged);
    }

    internal static void SuppressMacOsLiveRegion(StyledElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (AutomationProperties.GetLiveSetting(element) == AutomationLiveSetting.Off)
        {
            return;
        }

        // Avalonia 12.0.5 creates a native NSDictionary from the automation
        // name without checking whether an empty managed string converted to
        // nil. A workspace switch can clear a bound name while its control is
        // detached, so keep the stable name/help/focus metadata but suppress
        // the unsafe unsolicited announcement on macOS.
        AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Off);
    }

    private static void OnMacOsLiveSettingChanged(
        StyledElement element,
        AvaloniaPropertyChangedEventArgs _) =>
        SuppressMacOsLiveRegion(element);
}
