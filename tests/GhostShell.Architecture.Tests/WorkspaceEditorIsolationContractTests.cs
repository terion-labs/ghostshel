using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceEditorIsolationContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Workspace_editor_exposes_the_isolation_toggle_and_runtime_requirement()
    {
        var root = Assert.IsType<XElement>(LoadWorkspaceEditor().Root);
        var toggle = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Isolate workspace",
                StringComparison.Ordinal));

        Assert.Equal("{Binding IsIsolated, Mode=TwoWay}", AttributeValue(toggle, "IsChecked"));
        Assert.Equal(
            "{Binding CanToggleIsolation}",
            AttributeValue(toggle, "IsEnabled"));
        var isolationGroup = toggle.Ancestors().Single(element =>
            string.Equals(
                AttributeValue(element, "Heading"),
                "Isolation",
                StringComparison.Ordinal));
        Assert.Contains(
            "separate from the host and other workspaces",
            AttributeValue(isolationGroup, "Description"),
            StringComparison.Ordinal);
        var isolationToggleRow = toggle.Ancestors().Single(element =>
            string.Equals(
                AttributeValue(element, "Label"),
                "Isolate workspace",
                StringComparison.Ordinal));
        Assert.Contains(
            "execution and network boundary",
            AttributeValue(isolationToggleRow, "Description"),
            StringComparison.Ordinal);
        Assert.Contains(
            "blocked instead of using the host",
            AttributeValue(isolationToggleRow, "Description"),
            StringComparison.Ordinal);
        var runtimeRequirement = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding IsIsolationUnavailable}",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding IsolationRuntimeRequirementLabel}",
            AttributeValue(runtimeRequirement, "Label"));
        Assert.Equal(
            "{Binding IsolationRuntimeRequirementDescription}",
            AttributeValue(runtimeRequirement, "Description"));

        var imageRow = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Label"),
                "Runtime image",
                StringComparison.Ordinal));
        Assert.Equal("{Binding IsIsolated}", AttributeValue(imageRow, "IsVisible"));
        Assert.Contains(
            "minimal Ubuntu 24.04 image",
            AttributeValue(imageRow, "Description"),
            StringComparison.Ordinal);
        var image = FindAccessibleElement(imageRow, "Workspace isolation OCI image");
        Assert.Equal(
            "{Binding IsolationImageReference, Mode=TwoWay}",
            AttributeValue(image, "Text"));
        Assert.Equal(
            "Ubuntu 24.04 minimal (default)",
            AttributeValue(image, "PlaceholderText"));

        var install = FindAccessibleElement(
            runtimeRequirement,
            "{Binding InstallIsolationRuntimeAccessibleName}");
        Assert.Equal(
            "{Binding InstallIsolationRuntimeLabel}",
            AttributeValue(install, "Content"));
        Assert.Equal(
            "{Binding CanInstallIsolationRuntime}",
            AttributeValue(install, "IsVisible"));
        Assert.Equal(
            "OnInstallWorkspaceIsolationRuntimeClick",
            AttributeValue(install, "Click"));

        Assert.DoesNotContain(
            root.Descendants(),
            element => AttributeValue(element, "Text")?.Contains(
                "Preview",
                StringComparison.OrdinalIgnoreCase) == true);

        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Label"),
                "Host mounts locked while running",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Workspace_editor_exposes_source_guest_access_and_mount_actions()
    {
        var root = Assert.IsType<XElement>(LoadWorkspaceEditor().Root);
        var mountRow = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Label"),
                "Host mounts",
                StringComparison.Ordinal));

        Assert.Equal("{Binding IsIsolated}", AttributeValue(mountRow, "IsVisible"));
        Assert.Null(AttributeValue(mountRow, "IsEnabled"));
        Assert.Contains(
            "Saving mount changes restarts an open workspace",
            AttributeValue(mountRow, "Description"),
            StringComparison.Ordinal);
        Assert.Contains(
            mountRow.Descendants(),
            element => string.Equals(
                AttributeValue(element, "ItemsSource"),
                "{Binding IsolationMounts}",
                StringComparison.Ordinal));

        var hostPath = FindAccessibleElement(mountRow, "Host mount source directory");
        Assert.Equal("{Binding HostPath, Mode=TwoWay}", AttributeValue(hostPath, "Text"));
        var guestPath = FindAccessibleElement(mountRow, "Host mount guest path");
        Assert.Equal("{Binding GuestPath, Mode=TwoWay}", AttributeValue(guestPath, "Text"));
        var readOnly = FindAccessibleElement(mountRow, "Mount host path read only");
        Assert.Equal("{Binding IsReadOnly, Mode=TwoWay}", AttributeValue(readOnly, "IsChecked"));

        var add = FindAccessibleElement(mountRow, "Add host mount");
        Assert.Equal("OnAddIsolationMountClick", AttributeValue(add, "Click"));
        Assert.Equal("{Binding CanAddIsolationMount}", AttributeValue(add, "IsEnabled"));
        var remove = Assert.Single(
            mountRow.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "{Binding RemoveAccessibleName}",
                StringComparison.Ordinal));
        Assert.Equal("OnRemoveIsolationMountClick", AttributeValue(remove, "Click"));
        Assert.DoesNotContain(
            mountRow.Descendants(),
            element => AttributeValue(element, "Text")?.Contains(
                "macOS or Windows programs",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Workspace_settings_list_exposes_isolation_and_runtime_installation()
    {
        var root = Assert.IsType<XElement>(LoadSettings().Root);
        var toggle = FindAccessibleElement(
            root,
            "{Binding Name, StringFormat=Isolate {0} workspace}");
        Assert.Equal(
            "{Binding IsIsolated, Mode=OneWay}",
            AttributeValue(toggle, "IsChecked"));
        Assert.Equal(
            "{Binding CanToggleIsolation}",
            AttributeValue(toggle, "IsEnabled"));
        Assert.Equal(
            "OnWorkspaceIsolationChanged",
            AttributeValue(toggle, "IsCheckedChanged"));

        var install = FindAccessibleElement(root, "Install Apple container runtime");
        Assert.Equal(
            "OnInstallWorkspaceIsolationRuntimeClick",
            AttributeValue(install, "Click"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "Install Apple container to enable workspace isolation",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "Apple container is required",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Running_workspace_isolation_changes_require_the_restart_confirmation()
    {
        var views = Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views");
        var confirmation = File.ReadAllText(Path.Combine(views, "Confirmations.cs"));
        var settingsHandler = File.ReadAllText(Path.Combine(views, "MainWindow.Settings.cs"));
        var editorHandler = File.ReadAllText(Path.Combine(views, "MainWindow.axaml.cs"));

        Assert.Contains(
            "Workspace will be restarted to change isolation configuration.",
            confirmation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Confirmations.WorkspaceIsolationRestart(workspace.Name)",
            settingsHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.WorkspaceEditorImageChangeRebuildsIsolate(request)",
            editorHandler,
            StringComparison.Ordinal);
    }

    private static XElement FindAccessibleElement(XElement root, string accessibleName) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                accessibleName,
                StringComparison.Ordinal));

    private static XDocument LoadWorkspaceEditor() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "WorkspaceEditorView.axaml"));

    private static XDocument LoadSettings() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "SettingsView.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Name.LocalName,
                name,
                StringComparison.Ordinal))
            ?.Value;
}
