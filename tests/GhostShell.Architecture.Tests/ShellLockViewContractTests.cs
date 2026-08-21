using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class ShellLockViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Lock_boundary_uses_compiled_bindings_required_by_native_aot()
    {
        var mainWindow = LoadView("MainWindow");
        var lockView = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "ShellLockView");

        Assert.Equal(
            "{CompiledBinding ApplicationSecurityEditor}",
            AttributeValue(lockView, "DataContext"));
        Assert.Equal(
            "{CompiledBinding IsLocked}",
            AttributeValue(lockView, "IsVisible"));

        var component = LoadView(Path.Combine("Components", "ShellLockView"));
        Assert.Equal(
            "vm:ApplicationSecurityEditorViewModel",
            AttributeValue(component.Root!, "DataType"));

        Assert.DoesNotContain(
            component.Descendants().Attributes(),
            attribute => attribute.Value.StartsWith("{Binding ", StringComparison.Ordinal));

        var biometricButton = Assert.Single(
            component.Descendants(),
            element => AttributeValue(element, "Content")
                == "{CompiledBinding BiometricUnlockLabel}");
        Assert.Equal(
            "{CompiledBinding BiometricUnlockLabel}",
            AttributeValue(biometricButton, "Content"));
        Assert.Equal(
            "{CompiledBinding CanUseBiometrics}",
            AttributeValue(biometricButton, "IsVisible"));
    }

    [Fact]
    public void Release_aot_preserves_remaining_reflection_bound_ui_members()
    {
        var project = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "GhostShell.Desktop.csproj"));
        var root = Assert.Single(
            project.Descendants(),
            element => element.Name.LocalName == "TrimmerRootAssembly"
                && AttributeValue(element, "Include") == "GhostShell.App");
        var group = Assert.IsType<XElement>(root.Parent);

        Assert.Equal(
            "'$(GhostShellMacReleaseNativeAot)' == 'true'",
            AttributeValue(group, "Condition"));

        var workspaceEditor = LoadView("WorkspaceEditorView");
        Assert.Equal(
            "vm:WorkspaceEditorViewModel",
            AttributeValue(workspaceEditor.Root!, "DataType"));
        Assert.Contains(
            workspaceEditor.Descendants().Attributes(),
            attribute => attribute.Value
                == "{CompiledBinding AgentPolicy.IsEnabled}");
        Assert.Contains(
            workspaceEditor.Descendants().Attributes(),
            attribute => attribute.Value
                == "{CompiledBinding ValidationSummary}");

        var mainWindow = LoadView("MainWindow");
        var operationError = Assert.Single(
            mainWindow.Descendants().Attributes(),
            attribute => attribute.Value == "{CompiledBinding OperationError}");
        Assert.Equal("Text", operationError.Name.LocalName);

        var terminalPanel = LoadView(Path.Combine(
            "RuntimePanels",
            "TerminalRuntimePanelView"));
        Assert.Equal(
            "True",
            AttributeValue(terminalPanel.Root!, "CompileBindings"));
        Assert.Equal(
            "vm:TerminalRuntimePanelViewModel",
            AttributeValue(terminalPanel.Root!, "DataType"));
        Assert.Contains(
            terminalPanel.Descendants().Attributes(),
            attribute => attribute.Value == "{CompiledBinding SessionRequest}");
        Assert.Contains(
            terminalPanel.Descendants().Attributes(),
            attribute => attribute.Value == "{CompiledBinding HasConnectionOverlay}");
    }

    private static XDocument LoadView(string relativePath) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{relativePath}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
