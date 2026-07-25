using Avalonia.Automation;
using GhostShell.App.Controls;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class NativeTerminalApplicationKeyTests
{
    [Fact]
    public void Native_terminal_hosts_expose_named_live_status_to_accessibility_clients()
    {
        var presentation = new TerminalPresentationHost(TerminalPresentationKind.Native);
        var native = Assert.IsType<TerminalSessionHost>(presentation.Presentation);

        Assert.Equal("Interactive terminal", AutomationProperties.GetName(presentation));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(presentation));
        Assert.Equal("STARTING", AutomationProperties.GetItemStatus(presentation));
        Assert.Equal("Native interactive terminal", AutomationProperties.GetName(native));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(native));
        Assert.Equal("STARTING", AutomationProperties.GetItemStatus(native));
    }

    [Fact]
    public void PresentationPassesTheRuntimeOwnedDispatchStateToTheNativeHost()
    {
        var panelId = PanelInstanceId.New();
        var state = new TerminalStartupCommandDispatchState(
            panelId,
            ["deploy"],
            OperationContext.ForHuman(
                ClientId.New(),
                idempotencyKey: IdempotencyKey.New()),
            failurePolicy:
                StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var presentation = new TerminalPresentationHost(TerminalPresentationKind.Native)
        {
            StartupCommandDispatchState = state,
        };

        var native = Assert.IsType<TerminalSessionHost>(presentation.Presentation);

        Assert.Same(state, native.StartupCommandDispatchState);
    }

    [Fact]
    public void Native_presentation_forwards_the_synchronous_consume_decision()
    {
        var presentation = new TerminalPresentationHost(TerminalPresentationKind.Native);
        var native = Assert.IsType<TerminalSessionHost>(presentation.Presentation);
        NativeRendererKeyInput? observed = null;
        presentation.ApplicationKeyPressed += (_, e) =>
        {
            observed = e.Input;
            e.Handled = true;
        };
        var input = new NativeRendererKeyInput(
            new KeyStroke("B", KeyModifiers.Control),
            IsRepeat: false);

        var consumed = native.InterceptApplicationKey(input);

        Assert.True(consumed);
        Assert.Equal(input, observed);
    }

    [Fact]
    public void Native_key_passes_through_when_the_shell_does_not_handle_it()
    {
        var native = new TerminalSessionHost();
        native.ApplicationKeyPressed += (_, e) =>
            Assert.Equal("Q", e.Input.Stroke.Key);

        var consumed = native.InterceptApplicationKey(
            new NativeRendererKeyInput(new KeyStroke("Q"), IsRepeat: false));

        Assert.False(consumed);
    }

    [Fact]
    public void Repeat_from_a_previously_passed_through_press_cannot_enter_the_application_resolver()
    {
        var native = new TerminalSessionHost();
        var raised = false;
        native.ApplicationKeyPressed += (_, e) =>
        {
            raised = true;
            e.Handled = true;
        };

        var consumed = native.InterceptApplicationKey(
            new NativeRendererKeyInput(new KeyStroke("Q"), IsRepeat: true));

        Assert.False(consumed);
        Assert.False(raised);
    }
}
