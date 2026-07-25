namespace GhostShell.Application;

public static class SessionCapabilities
{
    public const string AttachRead = "session.attach.read";
    public const string AttachInteractive = "session.attach.interactive";
    public const string AgentContextInspect = "agent.context.inspect";
    public const string NativeRenderer = "terminal.renderer.native";
    public const string ManagedRenderer = "terminal.renderer.managed";
    public const string TerminalAgentInputBarrier = "terminal.agent_input_barrier";
    public const string TerminalReadScreen = "terminal.read_screen";
    public const string TerminalWrite = "terminal.write";
    public const string TerminalSendKeys = "terminal.send_keys";
    public const string TerminalSendChord = "terminal.send_chord";
    public const string TerminalEnter = "terminal.enter";
    public const string TerminalInterrupt = "terminal.interrupt";
    public const string TerminalWait = "terminal.wait";
    public const string TerminalMouse = "terminal.mouse";
    public const string TerminalScrollback = "terminal.scrollback";
    public const string TerminalClearScrollback = "terminal.scrollback.clear";
    public const string TerminalFind = "terminal.find";
    public const string TerminalSelection = "terminal.selection";
    public const string TerminalPaste = "terminal.paste";
    public const string TerminalResize = "terminal.resize";
    public const string TerminalFocus = "terminal.focus";
    public const string BrowserReadState = "browser.state.read";
    public const string BrowserSnapshot = "browser.snapshot";
    public const string BrowserClick = "browser.click";
    public const string BrowserFill = "browser.fill";
    public const string BrowserCheck = "browser.check";
    public const string BrowserNavigate = "browser.navigate";
    public const string BrowserBack = "browser.back";
    public const string BrowserForward = "browser.forward";
    public const string BrowserReload = "browser.reload";
    public const string BrowserStop = "browser.stop";
    public const string BrowserOriginGuard =
        "browser.navigation_origin_guard";
    public const string FilesList = "files.list";
    public const string FilesStat = "files.stat";
    public const string FilesPreview = "files.preview";
    public const string FilesCreateDirectory = "files.mkdir";
    public const string FilesRename = "files.rename";
    public const string FilesDelete = "files.delete";
    public const string FilesTransferEnqueue = "files.transfer.enqueue";
    public const string FilesTransferCancel = "files.transfer.cancel";
    public const string FilesTransferRetry = "files.transfer.retry";
    public const string StatisticsRead = "statistics.read";
    public const string ProcessesList = "processes.list";
    public const string InputLease = "session.input_lease";
}
