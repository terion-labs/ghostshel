using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// The badge text for a kind, in the interface's own register: sentence case,
/// with real acronyms kept as acronyms. Every view that labels a connection,
/// panel, or file provider reads from here, so a kind is never spelled two
/// ways — which is exactly what uppercasing enum names at each call site did.
/// </summary>
internal static class KindBadges
{
    public static string Connection(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Ssh => "SSH",
        ConnectionKind.Wsl => "WSL",
        _ => kind.ToString(),
    };

    public static string Panel(PanelKind kind) => kind switch
    {
        PanelKind.FileViewer => "Files",
        PanelKind.ProcessMonitor => "Process monitor",
        PanelKind.DatabaseViewer => "Database",
        PanelKind.Docker => "Docker",
        _ => kind.ToString(),
    };

    public static string Panel(ScreenPanelKind kind) => kind switch
    {
        ScreenPanelKind.FileViewer => "Files",
        ScreenPanelKind.ProcessMonitor => "Process monitor",
        ScreenPanelKind.DatabaseViewer => "Database",
        ScreenPanelKind.Docker => "Docker",
        _ => kind.ToString(),
    };

    public static string FileProvider(FileProviderKind kind) => kind switch
    {
        FileProviderKind.Local => "Local",
        FileProviderKind.S3 => "S3",
        FileProviderKind.Sftp => "SFTP",
        FileProviderKind.Ftp => "FTP/FTPS",
        FileProviderKind.Smb => "SMB",
        FileProviderKind.WebDav => "WebDAV",
        _ => kind.ToString(),
    };
}
