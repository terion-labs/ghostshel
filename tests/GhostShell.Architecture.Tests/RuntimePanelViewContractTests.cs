using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class RuntimePanelViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Theory]
    [InlineData(
        "TerminalRuntimePanelViewModel",
        "TerminalRuntimePanelView")]
    [InlineData(
        "BrowserRuntimePanelViewModel",
        "BrowserRuntimePanelView")]
    [InlineData(
        "FileRuntimePanelViewModel",
        "FileRuntimePanelView")]
    [InlineData(
        "StatisticsRuntimePanelViewModel",
        "StatisticsRuntimePanelView")]
    [InlineData(
        "ProcessMonitorRuntimePanelViewModel",
        "ProcessMonitorRuntimePanelView")]
    [InlineData(
        "UnavailableRuntimePanelViewModel",
        "UnavailableRuntimePanelView")]
    public void Main_window_templates_delegate_runtime_interaction_to_named_panel_views(
        string viewModel,
        string panelView)
    {
        var mainWindow = LoadView("MainWindow");
        var template = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && string.Equals(
                    AttributeValue(element, "DataType"),
                    $"vm:{viewModel}",
                    StringComparison.Ordinal));
        var component = Assert.Single(template.Elements());

        Assert.Equal(panelView, component.Name.LocalName);
        Assert.Equal(
            "OnRuntimePanelGotFocus",
            AttributeValue(component, "GotFocus"));
        Assert.Equal(
            "OnRuntimePanelPointerPressed",
            AttributeValue(component, "PointerPressed"));
        Assert.Equal(
            "OnCloseRuntimePanelClick",
            AttributeValue(component, "CloseRequested"));
        Assert.DoesNotContain(
            template.Descendants(),
            element => element.Name.LocalName == "SurfaceCard");
    }

    [Theory]
    [InlineData(
        "TerminalRuntimePanelView",
        null,
        "Close panel")]
    [InlineData(
        "BrowserRuntimePanelView",
        null,
        "Close browser panel")]
    [InlineData(
        "FileRuntimePanelView",
        null,
        "Close File Viewer panel")]
    [InlineData(
        "StatisticsRuntimePanelView",
        "Statistics panel",
        "Close Statistics panel")]
    [InlineData(
        "ProcessMonitorRuntimePanelView",
        "Process monitor panel",
        "Close Process Monitor panel")]
    [InlineData(
        "UnavailableRuntimePanelView",
        null,
        "Close panel")]
    public void Runtime_panel_views_preserve_focus_layout_and_close_contracts(
        string panelView,
        string? accessibleName,
        string closeName)
    {
        var document = LoadRuntimePanelView(panelView);
        var root = Assert.IsType<XElement>(document.Root);

        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.True(HasClass(root, "RuntimePanelFocusTarget"));
        Assert.Equal("True", AttributeValue(root, "Focusable"));
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));
        Assert.Equal(
            "{Binding IsVisibleInLayout}",
            AttributeValue(root, "IsVisible"));
        Assert.Equal(
            accessibleName,
            AttributeValue(root, "AutomationProperties.Name"));

        // A panel's chrome is the shell's card control, sunk into the page and
        // clipping what it holds, rather than a Border wearing a "PanelCard" class.
        var card = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "SurfaceCard"
                && string.Equals(
                    AttributeValue(element, "Tone"),
                    "Sunken",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding IsActive}",
            AttributeValue(card, "Classes.active"));
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "Border"
                && HasClass(element, "PanelHeader"));

        var close = Assert.Single(
            card.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    closeName,
                    StringComparison.Ordinal));
        Assert.Equal("OnCloseClick", AttributeValue(close, "Click"));

        var codeBehind = File.ReadAllText(RuntimePanelPath(panelView, ".axaml.cs"));
        Assert.Contains(
            "public event EventHandler<RoutedEventArgs>? CloseRequested;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "CloseRequested?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_focuses_the_single_runtime_panel_focus_target()
    {
        var codeBehind = ApplicationViews.FindPartialClassSources("MainWindow");

        Assert.Contains(
            "control.Classes.Contains(\"RuntimePanelFocusTarget\")",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "control.Classes.Contains(\"PanelCard\")",
            codeBehind,
            StringComparison.Ordinal);

        var mainWindow = LoadView("MainWindow");
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => HasClass(element, "RuntimePanelFocusTarget"));
    }

    [Fact]
    public void Empty_panel_reuses_the_new_item_catalog_without_workspaces()
    {
        var document = LoadRuntimePanelView("PanelPlaceholderView");
        var root = Assert.IsType<XElement>(document.Root);

        var chooser = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "NewItemChooserView");
        Assert.Equal("False", AttributeValue(chooser, "ShowWorkspaces"));
        Assert.Equal("False", AttributeValue(chooser, "ShowCloseAction"));
        Assert.Equal(
            "{Binding $parent[Window].DataContext}",
            AttributeValue(chooser, "DataContext"));
        Assert.Equal(
            "OnOpenConnectionClick",
            AttributeValue(chooser, "OpenConnectionRequested"));
        Assert.Equal(
            "OnOpenScreenClick",
            AttributeValue(chooser, "OpenScreenRequested"));
        Assert.Equal(
            "OnChooseTerminalClick",
            AttributeValue(chooser, "NewLocalTerminalRequested"));
        Assert.Equal(
            "OnChooseBrowserClick",
            AttributeValue(chooser, "NewBrowserRequested"));
        Assert.Equal(
            "OnChooseFileViewerClick",
            AttributeValue(chooser, "NewFileViewerRequested"));
        Assert.Equal(
            "OnChooseStatisticsClick",
            AttributeValue(chooser, "NewStatisticsRequested"));
        Assert.Equal(
            "OnChooseProcessMonitorClick",
            AttributeValue(chooser, "NewProcessMonitorRequested"));

        Assert.DoesNotContain(
            root.Descendants(),
            element => HasClass(element, "InlinePanelChooser"));
    }

    [Fact]
    public void Terminal_panel_view_preserves_native_host_and_typed_shell_interactions()
    {
        var mainWindow = LoadView("MainWindow");
        var template = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && string.Equals(
                    AttributeValue(element, "DataType"),
                    "vm:TerminalRuntimePanelViewModel",
                    StringComparison.Ordinal));
        var component = Assert.Single(template.Elements());

        Assert.Equal(
            "OnTerminalApplicationKeyPressed",
            AttributeValue(component, "ApplicationKeyPressed"));
        Assert.Equal(
            "OnCancelConnectionReconnectClick",
            AttributeValue(component, "CancelReconnectRequested"));
        Assert.Equal(
            "OnTerminalConnectionSelected",
            AttributeValue(component, "ConnectionSelected"));
        Assert.Equal(
            "OnTerminalNewConnectionRequested",
            AttributeValue(component, "NewConnectionRequested"));
        Assert.Equal(
            "OnRetryConnectionPanelClick",
            AttributeValue(component, "RetryConnectionRequested"));
        Assert.Equal(
            "OnTerminalSessionInitializationFailed",
            AttributeValue(component, "SessionInitializationFailed"));
        Assert.Equal(
            "OnTerminalSessionSnapshotChanged",
            AttributeValue(component, "SessionSnapshotChanged"));
        Assert.Equal(
            "OnTrustConnectionHostKeyClick",
            AttributeValue(component, "TrustHostKeyRequested"));

        var document = LoadRuntimePanelView("TerminalRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        var connectionSelector = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "PanelConnectionSelectorView");
        Assert.Equal(
            "{Binding $parent[Window].DataContext.PanelConnectionOptions}",
            AttributeValue(connectionSelector, "Options"));
        Assert.Equal(
            "{Binding ConnectionDisplayName}",
            AttributeValue(connectionSelector, "SelectedLabel"));
        Assert.Equal(
            "OnConnectionSelected",
            AttributeValue(connectionSelector, "ConnectionSelected"));
        Assert.Equal(
            "OnNewConnectionRequested",
            AttributeValue(connectionSelector, "NewConnectionRequested"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "Terminal",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding KindLabel}",
                StringComparison.Ordinal));
        var terminal = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "TerminalPresentationHost");

        Assert.Equal("RuntimeTerminal", AttributeValue(terminal, "Name"));
        Assert.Equal("1", AttributeValue(terminal, "Margin"));
        Assert.Equal("{Binding ClientId}", AttributeValue(terminal, "ClientId"));
        Assert.Equal(
            "{Binding SessionClient}",
            AttributeValue(terminal, "SessionClient"));
        Assert.Equal(
            "{Binding SessionRequest}",
            AttributeValue(terminal, "SessionRequest"));
        Assert.Equal(
            "OnApplicationKeyPressed",
            AttributeValue(terminal, "ApplicationKeyPressed"));
        Assert.Equal(
            "OnSessionSnapshotChanged",
            AttributeValue(terminal, "SessionSnapshotChanged"));
        Assert.Equal(
            "OnSessionInitializationFailed",
            AttributeValue(terminal, "SessionInitializationFailed"));
        Assert.DoesNotContain(
            root.DescendantsAndSelf(),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName.Contains("Transform", StringComparison.Ordinal)));

        var status = FindUniqueAccessibleElement(root, "Terminal session status");
        Assert.Equal(
            "Polite",
            AttributeValue(status, "AutomationProperties.LiveSetting"));

        var codeBehind = File.ReadAllText(
            RuntimePanelPath("TerminalRuntimePanelView", ".axaml.cs"));
        Assert.Contains(
            "ApplicationKeyPressed?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "SessionInitializationFailed?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "SessionSnapshotChanged?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_connection_selector_filters_saved_connections_and_reports_intent()
    {
        var path = Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            "PanelConnectionSelectorView.axaml");
        var root = Assert.IsType<XElement>(XDocument.Load(path).Root);
        var filter = FindUniqueAccessibleElement(root, "Filter saved connections");
        Assert.Equal("OnFilterTextChanged", AttributeValue(filter, "TextChanged"));
        Assert.Equal("Filter connections", AttributeValue(filter, "PlaceholderText"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding FilteredOptions, ElementName=Root}",
                    StringComparison.Ordinal));
        var create = FindUniqueAccessibleElement(
            root,
            "Create and connect a new connection");
        Assert.Equal("OnNewConnectionClick", AttributeValue(create, "Click"));

        var codeBehind = File.ReadAllText($"{path}.cs");
        Assert.Contains("connection.Name.Contains", codeBehind, StringComparison.Ordinal);
        Assert.Contains("connection.Kind.Contains", codeBehind, StringComparison.Ordinal);
        Assert.Contains("connection.Detail.Contains", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ISessionHostClient", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_panel_view_preserves_native_host_and_typed_shell_interactions()
    {
        var mainWindow = LoadView("MainWindow");
        var template = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && string.Equals(
                    AttributeValue(element, "DataType"),
                    "vm:BrowserRuntimePanelViewModel",
                    StringComparison.Ordinal));
        var component = Assert.Single(template.Elements());

        Assert.Equal(
            "OnBrowserAddressKeyDown",
            AttributeValue(component, "AddressKeyDown"));
        Assert.Equal(
            "OnBrowserBackClick",
            AttributeValue(component, "BackRequested"));
        Assert.Equal(
            "OnBrowserStateChanged",
            AttributeValue(component, "BrowserStateChanged"));
        Assert.Equal(
            "OnBrowserForwardClick",
            AttributeValue(component, "ForwardRequested"));
        Assert.Equal(
            "OnBrowserReloadClick",
            AttributeValue(component, "ReloadRequested"));
        Assert.Equal(
            "OnBrowserStopClick",
            AttributeValue(component, "StopRequested"));

        var document = LoadRuntimePanelView("BrowserRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        var browser = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "BrowserPresentationHost");

        Assert.Equal("RuntimeBrowser", AttributeValue(browser, "Name"));
        Assert.Equal("1", AttributeValue(browser, "Margin"));
        Assert.Equal("{Binding ClientId}", AttributeValue(browser, "ClientId"));
        Assert.Equal(
            "{Binding RendererView}",
            AttributeValue(browser, "RendererView"));
        Assert.Equal(
            "{Binding SessionClient}",
            AttributeValue(browser, "SessionClient"));
        Assert.Equal(
            "{Binding SessionRequest}",
            AttributeValue(browser, "SessionRequest"));
        Assert.Equal(
            "OnBrowserStateChanged",
            AttributeValue(browser, "BrowserStateChanged"));
        Assert.DoesNotContain(
            root.DescendantsAndSelf(),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName.Contains("Transform", StringComparison.Ordinal)));

        var address = FindUniqueAccessibleElement(root, "Browser address");
        Assert.Equal("OnAddressKeyDown", AttributeValue(address, "KeyDown"));
        // Bound by element name, not by data context. The row's data context is the
        // panel, because the shell resolves which panel to act on from the sender's
        // context — re-pointing the row at the browser host broke Close silently.
        Assert.Equal(
            "{Binding AddressText, ElementName=RuntimeBrowser, Mode=TwoWay}",
            AttributeValue(address, "Text"));
        Assert.Equal("about:blank", AttributeValue(address, "PlaceholderText"));
        Assert.Equal(
            "OnAddressGotFocus",
            AttributeValue(address, "GotFocus"));
        Assert.Equal(
            "OnAddressLostFocus",
            AttributeValue(address, "LostFocus"));

        var status = FindUniqueAccessibleElement(root, "Browser session status");
        Assert.Equal(
            "Polite",
            AttributeValue(status, "AutomationProperties.LiveSetting"));

        var codeBehind = File.ReadAllText(
            RuntimePanelPath("BrowserRuntimePanelView", ".axaml.cs"));
        // Raised with the presentation host, not the sender. The shell resolves
        // browser actions from it, and resolves Close from the panel's data
        // context — one context cannot answer both, so the view names the host.
        Assert.Contains(
            "AddressKeyDown?.Invoke(RuntimeBrowser, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "addressBox.PlaceholderText = null;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "addressBox.PlaceholderText = BlankAddressPlaceholder;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserStateChanged?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void File_panel_view_preserves_content_states_and_typed_shell_interactions()
    {
        var mainWindow = LoadView("MainWindow");
        var template = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && string.Equals(
                    AttributeValue(element, "DataType"),
                    "vm:FileRuntimePanelViewModel",
                    StringComparison.Ordinal));
        var component = Assert.Single(template.Elements());
        var shellInteractions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CreateFolderRequested"] = "OnFileCreateFolderClick",
            ["DeleteRequested"] = "OnFileDeleteClick",
            ["DismissOperationIssueRequested"] = "OnDismissFileOperationIssueClick",
            ["DownloadRequested"] = "OnFileDownloadClick",
            ["EntryDoubleTapped"] = "OnFileEntryDoubleTapped",
            ["EntrySelectionChanged"] = "OnFileEntrySelectionChanged",
            ["LoadMoreRequested"] = "OnFileLoadMoreClick",
            ["LocationKeyDown"] = "OnFileLocationKeyDown",
            ["NavigateUpRequested"] = "OnFileNavigateUpClick",
            ["OpenExternallyRequested"] = "OnFileOpenExternallyClick",
            ["RefreshRequested"] = "OnFileRefreshClick",
            ["RenameRequested"] = "OnFileRenameClick",
            ["TransferRequested"] = "OnFileTransferClick",
            ["UploadRequested"] = "OnFileUploadClick",
        };

        foreach (var (interaction, handler) in shellInteractions)
        {
            Assert.Equal(handler, AttributeValue(component, interaction));
        }

        var document = LoadRuntimePanelView("FileRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Grid"
                && string.Equals(
                    AttributeValue(element, "RowDefinitions"),
                    "36,Auto,*",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "Grid.Row"),
                    "1",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Padding"),
                    "{controls:Inset Left=Sm, Top=Xs, Right=Sm, Bottom=Sm}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Background"),
                    "{DynamicResource ShellSurfaceBrush}",
                    StringComparison.Ordinal));
        var connectionSelector = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "PanelConnectionSelectorView");
        Assert.Equal(
            "{Binding $parent[Window].DataContext.FileConnectionOptions}",
            AttributeValue(connectionSelector, "Options"));
        Assert.Equal(
            "{Binding ConnectionDisplayName}",
            AttributeValue(connectionSelector, "SelectedLabel"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "File provider profile",
                    StringComparison.Ordinal));
        Assert.Equal(
            3,
            root.Descendants().Count(element =>
                element.Name.LocalName == "ListBox"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding Entries}",
                    StringComparison.Ordinal)));
        var viewOptionsButton = FindUniqueAccessibleElement(
            root,
            "Open file sort and view options");
        var viewOptionsFlyout = Assert.Single(
            viewOptionsButton.Descendants(),
            element => element.Name.LocalName == "Flyout");
        Assert.Equal(
            3,
            viewOptionsFlyout.Descendants().Count(element =>
                element.Name.LocalName == "ComboBox"));
        Assert.DoesNotContain(
            root.Descendants().Where(element => element.Name.LocalName == "ComboBox"),
            element => !element.Ancestors().Contains(viewOptionsFlyout));
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding KindLabel}",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && HasClass(element, "FileToolbarGroup"));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Button"
                && HasClass(element, "FileToolbarAction"));
        var previewToggle = FindUniqueAccessibleElement(root, "Toggle file preview");
        Assert.Equal("OnTogglePreviewClick", AttributeValue(previewToggle, "Click"));
        Assert.True(HasClass(previewToggle, "InsetToggle"));
        Assert.Equal(
            "{Binding IsPreviewVisible}",
            AttributeValue(previewToggle, "Classes.active"));
        var previewSplitter = FindUniqueAccessibleElement(root, "Resize file preview");
        Assert.Equal(
            "{Binding IsPreviewVisible}",
            AttributeValue(previewSplitter, "IsVisible"));
        Assert.Equal("Columns", AttributeValue(previewSplitter, "ResizeDirection"));
        Assert.Equal(
            "PreviousAndNext",
            AttributeValue(previewSplitter, "ResizeBehavior"));
        foreach (var columnSplitterName in new[]
                 {
                     "Resize file name and size columns",
                     "Resize file size and modified columns",
                 })
        {
            var columnSplitter = FindUniqueAccessibleElement(root, columnSplitterName);
            Assert.Equal("Columns", AttributeValue(columnSplitter, "ResizeDirection"));
            Assert.Equal(
                "PreviousAndNext",
                AttributeValue(columnSplitter, "ResizeBehavior"));
        }

        var uploadButton = FindUniqueAccessibleElement(root, "Upload file");
        Assert.Equal("{Binding CanUpload}", AttributeValue(uploadButton, "IsVisible"));
        var itemCount = FindUniqueAccessibleElement(root, "File Viewer item count");
        Assert.Equal("{Binding Status}", AttributeValue(itemCount, "Value"));
        Assert.Equal(
            "{Binding HasListingSummary}",
            AttributeValue(itemCount, "IsVisible"));
        Assert.Equal(
            1,
            root.Descendants().Count(element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding LocationText}",
                StringComparison.Ordinal)));
        Assert.Equal(
            "OnLoadMoreClick",
            AttributeValue(
                FindUniqueAccessibleElement(root, "Load more files"),
                "Click"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "BOUNDED PREVIEW",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "BorderThickness"),
                "0,1,0,1",
                StringComparison.Ordinal));

        Assert.Equal(
            "Polite",
            AttributeValue(
                FindUniqueAccessibleElement(root, "File Viewer loading"),
                "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "Assertive",
            AttributeValue(
                FindUniqueAccessibleElement(root, "File Viewer operation status"),
                "AutomationProperties.LiveSetting"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding ShowErrorState}",
                StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding ShowEmptyLocationState}",
                StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding ShowSearchNoResultsState}",
                StringComparison.Ordinal));

        var codeBehind = File.ReadAllText(
            RuntimePanelPath("FileRuntimePanelView", ".axaml.cs"));
        foreach (var interaction in shellInteractions.Keys)
        {
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "StatisticsRuntimePanelView",
        "Statistics state",
        "Refresh statistics",
        "Statistics loading",
        "Statistics unavailable")]
    [InlineData(
        "ProcessMonitorRuntimePanelView",
        "Process monitor state",
        "Refresh processes",
        "Process monitor loading",
        "Process monitor unavailable")]
    public void Monitoring_panel_views_preserve_commands_and_live_state_announcements(
        string panelView,
        string statusName,
        string refreshName,
        string loadingName,
        string unavailableName)
    {
        var document = LoadRuntimePanelView(panelView);
        var root = Assert.IsType<XElement>(document.Root);

        var status = FindUniqueAccessibleElement(root, statusName);
        Assert.Equal("Polite", AttributeValue(status, "AutomationProperties.LiveSetting"));

        var refresh = FindUniqueAccessibleElement(root, refreshName);
        Assert.Equal("Button", refresh.Name.LocalName);
        Assert.Equal(
            "{Binding RefreshCommand}",
            AttributeValue(refresh, "Command"));

        var loading = FindUniqueAccessibleElement(root, loadingName);
        Assert.Equal("Polite", AttributeValue(loading, "AutomationProperties.LiveSetting"));

        var unavailable = FindUniqueAccessibleElement(root, unavailableName);
        Assert.Equal(
            "Assertive",
            AttributeValue(unavailable, "AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void Statistics_panel_presents_host_metrics_with_bounded_history_charts()
    {
        var document = LoadRuntimePanelView("StatisticsRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        var charts = root
            .Descendants()
            .Where(element => element.Name.LocalName == "TimeSeriesChart")
            .ToArray();

        Assert.Equal(2, charts.Length);
        Assert.Contains(
            charts,
            chart => AttributeValue(chart, "Values") == "{Binding CpuHistory}"
                && AttributeValue(chart, "Maximum") == "100");
        Assert.Contains(
            charts,
            chart => AttributeValue(chart, "Values") == "{Binding MemoryHistory}");

        var visibleCopy = string.Join(
            " ",
            root.Descendants()
                .Select(element => AttributeValue(element, "Text"))
                .OfType<string>());
        Assert.DoesNotContain("OBSERVED", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GHOSTSHELL CPU", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GHOSTSHELL MEMORY", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CPU USAGE", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("PROCESS MEMORY", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("RUNNING PROCESSES", visibleCopy, StringComparison.Ordinal);
    }

    private static XElement FindUniqueAccessibleElement(
        XElement root,
        string accessibleName) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                accessibleName,
                StringComparison.Ordinal));

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadRuntimePanelView(string panelView) =>
        XDocument.Load(RuntimePanelPath(panelView, ".axaml"));

    private static string RuntimePanelPath(string panelView, string extension) =>
        Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "RuntimePanels",
            $"{panelView}{extension}");

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == name)
            ?.Value;

    private static bool HasClass(XElement element, string className) =>
        (AttributeValue(element, "Classes") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.Ordinal);
}
