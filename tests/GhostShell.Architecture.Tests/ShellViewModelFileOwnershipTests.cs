using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class ShellViewModelFileOwnershipTests
{
    [Fact]
    public void Launcher_and_history_types_live_outside_the_mixed_shell_module()
    {
        var shell = ReadViewModelSource("ShellViewModels.cs");
        var launcher = ReadViewModelSource("LauncherViewModels.cs");
        var history = ReadViewModelSource("RecentSessionHistoryViewModels.cs");

        string[] launcherDeclarations =
        [
            "public sealed class LauncherWorkspaceViewModel",
            "public sealed record LauncherConnectionViewModel",
            "public sealed record LauncherScreenViewModel",
            "public sealed record LauncherScreenPanelPreviewViewModel",
            "public enum LauncherSearchResultKind",
            "public abstract record LauncherSearchTarget",
            "public sealed record LauncherSearchResultViewModel",
        ];
        foreach (var declaration in launcherDeclarations)
        {
            Assert.Contains(declaration, launcher, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, shell, StringComparison.Ordinal);
        }

        string[] historyDeclarations =
        [
            "public enum HistoryExportScope",
            "public sealed record HistoryRetentionOption",
        ];
        foreach (var declaration in historyDeclarations)
        {
            Assert.Contains(declaration, history, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, shell, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Settings_presentation_types_live_outside_the_mixed_shell_module()
    {
        var shell = ReadViewModelSource("ShellViewModels.cs");
        var settings = ReadViewModelSource("SettingsPresentationViewModels.cs");

        string[] settingsDeclarations =
        [
            "public sealed record AnsiSwatchViewModel",
            "public sealed record LayoutCardViewModel",
            "public sealed record ProductComponentViewModel",
            "public sealed record KeybindingRowViewModel",
            "public sealed record ThemeChromePreference",
        ];
        foreach (var declaration in settingsDeclarations)
        {
            Assert.Contains(declaration, settings, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, shell, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Runtime_types_remain_in_the_shell_module_until_the_runtime_slice()
    {
        var shell = ReadViewModelSource("ShellViewModels.cs");

        Assert.Contains(
            "public sealed class RuntimeWorkspaceViewModel",
            shell,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed class RuntimeTabViewModel",
            shell,
            StringComparison.Ordinal);
        Assert.Contains(
            "public abstract class RuntimePanelViewModel",
            shell,
            StringComparison.Ordinal);
    }

    private static string ReadViewModelSource(string fileName) =>
        File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "GhostShell.App",
            "ViewModels",
            fileName));
}
