using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class RuntimePanelViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    /// <summary>
    /// Every panel wears the same chrome, and it comes from the component rather
    /// than from the view.
    ///
    /// It used to be written out in each of these eight views, and had drifted the
    /// way duplicated markup does. The browser declared its split buttons with no
    /// <c>Grid.Column</c>, so they rendered stacked on top of its status dot where
    /// nobody could press them — the panel looked as though it simply had no split
    /// controls. Its two split icons were also swapped relative to the other seven,
    /// and two views set a header height the other six did not.
    ///
    /// None of that is reachable from a view any more, which is what this asserts.
    /// </summary>
    [Theory]
    [InlineData("TerminalRuntimePanelView")]
    [InlineData("BrowserRuntimePanelView")]
    [InlineData("FileRuntimePanelView")]
    [InlineData("StatisticsRuntimePanelView")]
    [InlineData("ProcessMonitorRuntimePanelView")]
    [InlineData("DatabaseRuntimePanelView")]
    [InlineData("UnavailableRuntimePanelView")]
    [InlineData("PanelPlaceholderView")]
    public void Panels_take_their_chrome_from_the_component_rather_than_drawing_it(
        string viewName)
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath(viewName, ".axaml")).Root);
        var chrome = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "PanelChrome");

        Assert.Equal("OnCloseClick", AttributeValue(chrome, "CloseRequested"));
        Assert.Equal("OnSplitRequested", AttributeValue(chrome, "SplitRequested"));
        Assert.Equal("{Binding IsActive}", AttributeValue(chrome, "IsActive"));
        Assert.Equal("{Binding IsZoomed}", AttributeValue(chrome, "IsZoomed"));
        Assert.NotNull(AttributeValue(chrome, "Title"));

        foreach (var owned in new[] { "PanelDockHandle", "SurfaceCard" })
        {
            Assert.DoesNotContain(
                root.Elements(),
                element => element.Name.LocalName == owned);
        }

        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && HasClass(element, "PanelHeader"));

        // Nor the other end of it. Two panels drew the same footer by hand —
        // surface fill, a hairline along the top, muted nine-point text — which
        // is how the header started before it was one component. A footer is a
        // slot now, and the browser needs it to be exactly the strip the
        // component draws: its page covers everything else the panel has.
        //
        // Along the bottom of the panel itself, which a strip inside some pane of
        // it is not — the file panel's preview toggles sit on the same hairline
        // and are nobody's footer.
        Assert.DoesNotContain(
            chrome.Elements().SelectMany(content => content.Elements()),
            element => element.Name.LocalName == "Border"
                && AttributeValue(element, "BorderThickness") == "0,1,0,0"
                && AttributeValue(element, "Background")
                    == "{DynamicResource ShellSurfaceBrush}");
        Assert.DoesNotContain(
            root.Descendants(),
            element => AttributeValue(element, "AutomationProperties.Name")
                is "Split this panel left and right"
                or "Split this panel top and bottom");
    }

    /// <summary>
    /// And the component draws it: the card, the title as Dock's drag surface so
    /// panels need no second tab strip, and split/split/close in that order.
    /// </summary>
    [Fact]
    public void The_panel_component_owns_the_card_the_drag_handle_and_the_shared_actions()
    {
        var theme = Assert.Single(
            DesignSystem().Descendants(),
            element => element.Name.LocalName == "ControlTheme"
                && string.Equals(
                    AttributeValue(element, "TargetType"),
                    "controls:PanelChrome",
                    StringComparison.Ordinal));

        var card = Assert.Single(
            theme.Descendants(),
            element => element.Name.LocalName == "SurfaceCard");
        Assert.Equal("Panel", AttributeValue(card, "Tone"));
        Assert.Equal("True", AttributeValue(card, "ClipsContent"));
        Assert.Equal(
            "{TemplateBinding IsActive}",
            AttributeValue(card, "Classes.active"));

        // The title is the drag surface. It no longer needs a hand-set data
        // context: a handle declared in a template cannot know where it will be
        // used, so PanelDockHandle finds its dockable by walking to the ancestor
        // that holds one.
        var handle = Assert.Single(
            theme.Descendants(),
            element => element.Name.LocalName == "PanelDockHandle");
        Assert.Null(AttributeValue(handle, "DataContext"));

        var handleSource = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Controls",
            "PanelDockHandle.cs"));
        // It points itself at that dockable. Dock reads the drag source's own
        // data context to decide what is being dragged, so a handle whose context
        // is something else is not a drag surface at all. Moving the handle into
        // the component dropped the binding each view used to set by hand, and
        // nothing failed — panels just stopped being draggable.
        Assert.Contains(
            "Bind(DataContextProperty, host.GetObservable(DataContextProperty))",
            handleSource,
            StringComparison.Ordinal);

        var actions = theme
            .Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Select(element => AttributeValue(element, "Name") ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "PART_Float",
                "PART_Dock",
                "PART_SplitLeftRight",
                "PART_SplitTopBottom",
                "PART_Close",
            ],
            actions);

        // Float and dock are one control in two states, so exactly one of them is
        // ever offered. Floating used to be a double-click on the title — nothing
        // announced it, and nothing undid it.
        var floatOut = theme.Descendants().Single(element =>
            AttributeValue(element, "Name") == "PART_Float");
        var dockBack = theme.Descendants().Single(element =>
            AttributeValue(element, "Name") == "PART_Dock");
        Assert.Equal("{TemplateBinding CanFloat}", AttributeValue(floatOut, "IsVisible"));
        Assert.Equal("{TemplateBinding IsFloating}", AttributeValue(dockBack, "IsVisible"));

        // A panel says what it is doing along its bottom, and for one holding an
        // operating-system view that strip is the only part of the panel the
        // shell still draws — such a view is composited above every Avalonia
        // pixel. Anything the browser panel needs seen or pressed lives there,
        // including the corner a floating panel is resized from, so the footer
        // is load-bearing rather than decorative.
        var footer = Assert.Single(
            theme.Descendants(),
            element => element.Name.LocalName == "Border"
                && HasClass(element, "PanelFooter"));
        Assert.Equal("{TemplateBinding IsFooterVisible}", AttributeValue(footer, "IsVisible"));

        var browser = XDocument.Load(RuntimePanelPath("BrowserRuntimePanelView", ".axaml"));
        Assert.Contains(
            browser.Descendants(),
            element => element.Name.LocalName == "PanelChrome.Footer");

        // The browser drew the two splits the other way round for as long as it
        // drew them itself.
        var splits = theme
            .Descendants()
            .Where(element => element.Name.LocalName == "SymbolIcon")
            .Select(element => AttributeValue(element, "Symbol") ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "WindowMultiple",
                "WindowMultipleOff",
                "SplitVertical",
                "SplitHorizontal",
                "Dismiss",
            ],
            splits);

        var chrome = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Controls",
            "PanelChrome.cs"));
        // Raised as the chrome, not as the button inside its template: the shell
        // resolves which panel to act on from the sender's data context, and the
        // chrome's is the panel.
        Assert.Contains(
            "CloseRequested?.Invoke(this, e);",
            chrome,
            StringComparison.Ordinal);
        Assert.Contains(
            "SplitRequested?.Invoke(this, PanelSplitOrientation.LeftRight);",
            chrome,
            StringComparison.Ordinal);
        // Float is asked for, not done. A panel floats inside the shell's window
        // — a panel holding an operating-system view cannot change window without
        // that view being destroyed — so which panels are floating is the
        // workspace's to know, and the chrome only says which one asked.
        Assert.Contains(
            "RaiseEvent(new RoutedEventArgs(FloatToggleRequestedEvent, this));",
            chrome,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsFloating = FloatingPanelLayer.For(this) is not null;",
            chrome,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FloatDockable", chrome, StringComparison.Ordinal);

        // Nothing floats on a gesture nobody can see.
        Assert.DoesNotContain("DoubleTapped", handleSource, StringComparison.Ordinal);
    }

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
        "DatabaseRuntimePanelViewModel",
        "DatabaseRuntimePanelView")]
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
        "DatabaseRuntimePanelView",
        "Database panel",
        "Close database panel")]
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

        // A panel's chrome is the shared component, which draws the card on the
        // panel surface — the same one the workspaces sidebar uses. What closing
        // this particular panel is called is the one part of it the panel says.
        var chrome = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "PanelChrome");
        Assert.Equal(closeName, AttributeValue(chrome, "CloseLabel") ?? "Close panel");
        Assert.Equal("OnCloseClick", AttributeValue(chrome, "CloseRequested"));

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
    public void Empty_panel_reuses_the_new_item_catalog()
    {
        var document = LoadRuntimePanelView("PanelPlaceholderView");
        var root = Assert.IsType<XElement>(document.Root);

        var chooser = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "LauncherView");
        // The cell has its own close button in its header, so the catalog does
        // not offer a second one.
        Assert.Equal("False", AttributeValue(chooser, "ShowCloseAction"));
        Assert.Equal(
            "{Binding $parent[Window].DataContext}",
            AttributeValue(chooser, "DataContext"));
        Assert.Equal(
            "OnConnectionLaunchRequested",
            AttributeValue(chooser, "ConnectionLaunchRequested"));
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
    public void Terminal_panel_view_preserves_managed_host_and_typed_shell_interactions()
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
        Assert.Equal(
            "Terminal",
            AttributeValue(
                Assert.Single(
                    root.Elements(),
                    element => element.Name.LocalName == "PanelChrome"),
                "Title"));
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
            // The file actions arrive as one event that names which was asked
            // for, rather than one event each. They are a list the panel builds
            // from what the connection can do, and a list cannot be turned into
            // a fixed set of events without going back to hand-written markup
            // per action — which is what left the toolbar and its overflow menu
            // disagreeing about what was possible.
            ["ActionRequested"] = "OnFileActionRequested",
            ["DismissOperationIssueRequested"] = "OnDismissFileOperationIssueClick",
            ["EntryDoubleTapped"] = "OnFileEntryDoubleTapped",
            ["EntrySelectionChanged"] = "OnFileEntrySelectionChanged",
            ["EntryTransferDropRequested"] = "OnFileEntryTransferDropRequested",
            ["EntryTransferKeyRequested"] = "OnFileEntryTransferKeyRequested",
            ["LoadMoreRequested"] = "OnFileLoadMoreClick",
            ["LocationKeyDown"] = "OnFileLocationKeyDown",
            ["NavigateUpRequested"] = "OnFileNavigateUpClick",
            ["RefreshRequested"] = "OnFileRefreshClick",
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
                    "Auto,*",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "Border"
                && string.Equals(
                    AttributeValue(element, "Grid.Row"),
                    "0",
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
        var fileNameLabels = root.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock"
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding Name}",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, fileNameLabels.Length);
        Assert.All(fileNameLabels, label =>
        {
            Assert.Equal("CharacterEllipsis", AttributeValue(label, "TextTrimming"));
            Assert.Equal("{Binding Name}", AttributeValue(label, "ToolTip.Tip"));
        });
        Assert.Contains(
            fileNameLabels,
            label => label.Parent?.Name.LocalName == "Grid"
                && string.Equals(
                    AttributeValue(label, "Grid.Column"),
                    "1",
                    StringComparison.Ordinal));
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

        // No action is written out by hand. The strip and the overflow menu are
        // the same list of actions shown twice, so an action a connection
        // cannot perform leaves both at once and neither can be left behind.
        var actionStrip = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding ToolbarActions}",
                    StringComparison.Ordinal));
        Assert.Contains(
            actionStrip.Descendants(),
            element => element.Name.LocalName == "Button"
                && string.Equals(
                    AttributeValue(element, "Command"),
                    "{Binding Command}",
                    StringComparison.Ordinal));

        // One right-click menu over all three layouts and over the space below
        // the last row, filled when it opens from where the press landed.
        // Three menus in the markup would be three places to add the next
        // action to, which is the shape this replaced.
        var contextMenu = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "ContextMenu");
        Assert.Equal("OnFileContextMenuOpening", AttributeValue(contextMenu, "Opening"));
        foreach (var menu in new[] { contextMenu, OverflowMenu(root) })
        {
            Assert.Contains(
                menu.Descendants(),
                element => element.Name.LocalName == "Setter"
                    && string.Equals(
                        AttributeValue(element, "Property"),
                        "Command",
                        StringComparison.Ordinal));
            Assert.Contains(
                menu.Descendants(),
                element => element.Name.LocalName == "Setter"
                    && string.Equals(
                        AttributeValue(element, "Property"),
                        "IsEnabled",
                        StringComparison.Ordinal));
        }

        // And the toolbar answers to the panel's own width, not the window's:
        // two of these can share one window at different widths.
        var containerQuery = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "ContainerQuery");
        Assert.Equal("filePanel", AttributeValue(containerQuery, "Name"));
        Assert.Equal("Width", AttributeValue(root, "Container.Sizing"));

        var itemCount = FindUniqueAccessibleElement(root, "File Viewer item count");
        Assert.Equal("{Binding Status}", AttributeValue(itemCount, "Value"));
        Assert.Equal("{Binding ShortStatus}", AttributeValue(itemCount, "CompactValue"));
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
        var transferDropTarget = FindUniqueAccessibleElement(
            root,
            "File transfer drop target");
        Assert.Equal("Rectangle", transferDropTarget.Name.LocalName);
        Assert.Equal("False", AttributeValue(transferDropTarget, "IsVisible"));
        Assert.Equal("4,3", AttributeValue(transferDropTarget, "StrokeDashArray"));
        Assert.Equal(
            "{DynamicResource ShellCardRadius}",
            AttributeValue(transferDropTarget, "RadiusX"));
        Assert.Equal(
            "{DynamicResource ShellCardRadius}",
            AttributeValue(transferDropTarget, "RadiusY"));
        var folderDropStyle = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Style"
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "ListBoxItem.transferDropTarget /template/ ContentPresenter",
                    StringComparison.Ordinal));
        Assert.Contains(
            folderDropStyle.Elements(),
            element => element.Name.LocalName == "Setter"
                && string.Equals(
                    AttributeValue(element, "Property"),
                    "Background",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            folderDropStyle.Elements(),
            element => element.Name.LocalName == "Setter"
                && AttributeValue(element, "Property") is
                    "BorderBrush" or "BorderThickness");

        var application = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "App.axaml"));
        var scalarCardRadius = Assert.Single(
            application.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Key"),
                "ShellCardRadius",
                StringComparison.Ordinal));
        Assert.Equal("Double", scalarCardRadius.Name.LocalName);

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
        foreach (var interaction in shellInteractions.Keys.Except(
                     [
                         "ActionRequested",
                         "EntryTransferDropRequested",
                         "EntryTransferKeyRequested",
                     ],
                     StringComparer.Ordinal))
        {
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind,
                StringComparison.Ordinal);
        }

        // The action carries which action it is, so it is raised with an
        // argument the view builds rather than one it was handed.
        Assert.Contains(
            "ActionRequested?.Invoke(this, new FilePanelActionEventArgs(action));",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "EntryTransferKeyRequested?.Invoke(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "EntryTransferDropRequested?.Invoke(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void OnFilePointerMoved",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ActiveFileDrag(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.ShowDragGhost(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.True(
            codeBehind.IndexOf(
                "candidate.Pointer.Capture(this);",
                StringComparison.Ordinal)
            < codeBehind.IndexOf(
                "_activeFileDrag = activeDrag;",
                StringComparison.Ordinal));
        Assert.Contains(
            "ResolveInternalFileDropTarget(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DragDrop.DoDragDropAsync(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveFileDropTarget(e.Source, e.DataTransfer)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "DestinationFolder",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private async ", codeBehind, StringComparison.Ordinal);
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
        Assert.Contains("CPU usage", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("Process memory", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("Running processes", visibleCopy, StringComparison.Ordinal);
    }

    private static XElement OverflowMenu(XElement root) => Assert.Single(
        root.Descendants(),
        element => element.Name.LocalName == "MenuFlyout");

    private static XElement FindUniqueAccessibleElement(
        XElement root,
        string accessibleName) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                accessibleName,
                StringComparison.Ordinal));

    private static XDocument DesignSystem() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "DesignSystem.axaml"));

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
