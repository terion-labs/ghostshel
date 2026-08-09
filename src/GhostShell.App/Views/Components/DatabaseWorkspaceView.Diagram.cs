using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace GhostShell.App.Views.Components;

public sealed partial class DatabaseWorkspaceView
{
    private static readonly FilePickerFileType MermaidMarkdownFileType = new("Mermaid Markdown")
    {
        Patterns = ["*.md"],
        MimeTypes = ["text/markdown"],
        AppleUniformTypeIdentifiers = ["net.daringfireball.markdown"],
    };

    private async void OnCopyMermaidDiagramClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is not { HasMermaidDiagram: true } panel
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(panel.MermaidDiagramText);
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not copy the Mermaid diagram: {exception.Message}");
        }
    }

    private async void OnCopyMermaidSvgClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is not { } panel
            || !MermaidDiagramRenderer.HasRenderedDiagram
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(MermaidDiagramRenderer.RenderedSvg);
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not copy the rendered SVG: {exception.Message}");
        }
    }

    private async void OnSaveMermaidDiagramClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var panel = Panel;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (panel is not { HasMermaidDiagram: true } || storage?.CanSave != true)
        {
            return;
        }

        var source = panel.MermaidDiagramText;
        try
        {
            var selected = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the Mermaid ER diagram",
                SuggestedFileName = "dbdiagram.md",
                DefaultExtension = "md",
                FileTypeChoices = [MermaidMarkdownFileType],
                ShowOverwritePrompt = true,
            });
            if (selected is null)
            {
                return;
            }

            if (!string.Equals(source, panel.MermaidDiagramText, StringComparison.Ordinal))
            {
                panel.ReportInteractionError(
                    "The database diagram changed while the destination was open. Save it again.");
                return;
            }

            var content = Encoding.UTF8.GetBytes(source);
            await WriteStorageFileAsync(
                selected,
                destination => destination.WriteAsync(content).AsTask());
        }
        catch (OperationCanceledException)
        {
            // Native save pickers do not agree on whether cancellation returns
            // null or throws. Both mean the user intentionally did nothing.
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not save the Mermaid diagram: {exception.Message}");
        }
    }
}
