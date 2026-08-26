using System.Collections.ObjectModel;
using System.Reflection;
using GhostShell.App;
using GhostShell.App.ViewModels;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class DefinitionSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_definition_settings_owner_without_editor_state_fields()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.DefinitionSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(DefinitionSettingsViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(DefinitionSettingsViewModel));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(LayoutDesignerViewModel));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(KeybindingEditorSessionViewModel));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(KeybindingProfileItemViewModel));
        Assert.DoesNotContain(fields, field =>
            field.FieldType.IsGenericType
            && field.FieldType.GetGenericTypeDefinition() == typeof(ObservableCollection<>)
            && field.FieldType.GenericTypeArguments[0] is var itemType
            && (itemType == typeof(LayoutCardViewModel)
                || itemType == typeof(KeybindingRowViewModel)
                || itemType == typeof(KeybindingProfileItemViewModel)));
    }

    [Fact]
    public void Definition_settings_owner_contains_projection_mutation_and_lifetime_policy()
    {
        Assert.Contains(typeof(IDisposable), typeof(DefinitionSettingsViewModel).GetInterfaces());
        Assert.NotNull(typeof(DefinitionSettingsViewModel).GetMethod(
            nameof(DefinitionSettingsViewModel.ApplyCatalog)));
        Assert.NotNull(typeof(DefinitionSettingsViewModel).GetMethod(
            nameof(DefinitionSettingsViewModel.SaveLayoutDesignerAsync)));
        Assert.NotNull(typeof(DefinitionSettingsViewModel).GetMethod(
            nameof(DefinitionSettingsViewModel.SaveKeybindingEditorAsync)));

        var root = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "GhostShell.App",
            "ViewModels",
            "MainWindowViewModel.cs"));
        Assert.DoesNotContain("private void RefreshKeybindings", root, StringComparison.Ordinal);
        Assert.DoesNotContain("private void OpenKeybindingEditor", root, StringComparison.Ordinal);
        Assert.DoesNotContain("private bool CanReplaceLayoutDesigner", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_layoutDesignerEditor", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_keybindingEditorSession", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectedKeybindingProfile", root, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_settings_presentation_does_not_escape_the_ui_context()
    {
        var source = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "GhostShell.App",
            "ViewModels",
            "DefinitionSettingsViewModel.cs"));

        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", source, StringComparison.Ordinal);
    }
}
