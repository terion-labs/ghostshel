using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// The three families of saved connections the shell manages through one UI:
/// terminal endpoints, file-transfer providers, and database connections.
/// </summary>
public enum SavedConnectionFamily
{
    Terminal,
    Files,
    Database,
}

/// <summary>
/// One entry in the unified connection-type selector. Exactly one of the
/// family-specific payloads is set.
/// </summary>
public sealed record UnifiedConnectionTypeOption(
    SavedConnectionFamily Family,
    string Group,
    string Label,
    ConnectionKind? TerminalKind = null,
    FileProviderKind? FileKind = null,
    string? DatabaseDriverId = null)
{
    public string DisplayName => $"{Group} · {Label}";
}

/// <summary>What the unified editor produced, discriminated by family.</summary>
public abstract record UnifiedConnectionEditorResult
{
    private UnifiedConnectionEditorResult()
    {
    }

    /// <summary>
    /// A terminal profile. <paramref name="SaveConnection"/> is false only for
    /// the connect purpose with its save checkbox cleared: the profile opens a
    /// session but is not persisted.
    /// </summary>
    public sealed record Terminal(
        ConnectionEditorSaveRequest Request,
        bool SaveConnection) : UnifiedConnectionEditorResult;

    public sealed record Files(
        FileProviderProfileSaveRequest Request) : UnifiedConnectionEditorResult;

    public sealed record Database(
        DatabaseConnectionSaveRequest Request) : UnifiedConnectionEditorResult;
}

/// <summary>
/// Hosts the three family editors behind one name field and one grouped
/// connection-type selector. Families whose runtime is unavailable in the
/// current build simply do not contribute options.
/// </summary>
public sealed class UnifiedConnectionEditorViewModel : ObservableObject
{
    private UnifiedConnectionTypeOption _selectedType;

    public UnifiedConnectionEditorViewModel(
        ConnectionEditorViewModel terminal,
        FileProviderProfileEditorViewModel? files,
        DatabaseConnectionEditorViewModel? database,
        SavedConnectionFamily? lockedFamily = null,
        SavedConnectionFamily initialFamily = SavedConnectionFamily.Terminal)
    {
        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        Files = files;
        Database = database;
        LockedFamily = lockedFamily;
        TypeOptions = BuildTypeOptions(lockedFamily);
        if (TypeOptions.Count == 0)
        {
            throw new InvalidOperationException(
                "No connection family is available for this editor.");
        }

        _selectedType = InitialOption(lockedFamily ?? initialFamily);
    }

    public ConnectionEditorViewModel Terminal { get; }

    public FileProviderProfileEditorViewModel? Files { get; }

    public DatabaseConnectionEditorViewModel? Database { get; }

    /// <summary>Editing an existing definition pins the editor to its family.</summary>
    public SavedConnectionFamily? LockedFamily { get; }

    public IReadOnlyList<UnifiedConnectionTypeOption> TypeOptions { get; }

    public UnifiedConnectionTypeOption SelectedType
    {
        get => _selectedType;
        set
        {
            if (!SetProperty(ref _selectedType, value) || value is null)
            {
                return;
            }

            ApplyOption(value);
            OnPropertyChanged(nameof(IsTerminal));
            OnPropertyChanged(nameof(IsFiles));
            OnPropertyChanged(nameof(IsDatabase));
            OnPropertyChanged(nameof(CanTest));
            OnPropertyChanged(nameof(TestLabel));
            OnPropertyChanged(nameof(Name));
        }
    }

    public SavedConnectionFamily Family => SelectedType.Family;

    public bool IsTerminal => Family == SavedConnectionFamily.Terminal;

    public bool IsFiles => Family == SavedConnectionFamily.Files;

    public bool IsDatabase => Family == SavedConnectionFamily.Database;

    /// <summary>Databases have no bounded probe yet; opening validates them.</summary>
    public bool CanTest => !IsDatabase;

    public string TestLabel => IsFiles ? "Test" : "Run diagnostics";

    public bool IsEditing => LockedFamily switch
    {
        SavedConnectionFamily.Terminal => Terminal.IsEditing,
        SavedConnectionFamily.Files => Files?.IsEditing == true,
        SavedConnectionFamily.Database => Database?.IsEditing == true,
        _ => false,
    };

    public string EditorTitle => IsEditing ? "Edit connection" : "New connection";

    /// <summary>
    /// One name for whichever definition the dialog produces. Writing through
    /// to every family keeps the typed name when the type selection switches
    /// families.
    /// </summary>
    public string Name
    {
        get => Family switch
        {
            SavedConnectionFamily.Files => Files?.Name ?? string.Empty,
            SavedConnectionFamily.Database => Database?.Name ?? string.Empty,
            _ => Terminal.Name,
        };
        set
        {
            Terminal.Name = value;
            if (Files is not null)
            {
                Files.Name = value;
            }

            if (Database is not null)
            {
                Database.Name = value;
            }

            OnPropertyChanged();
        }
    }

    public UnifiedConnectionEditorResult CreateSaveResult(bool saveConnection = true) =>
        Family switch
        {
            SavedConnectionFamily.Terminal => new UnifiedConnectionEditorResult.Terminal(
                Terminal.CreateSaveRequest(),
                saveConnection),
            SavedConnectionFamily.Files => new UnifiedConnectionEditorResult.Files(
                Files!.CreateSaveRequest()),
            SavedConnectionFamily.Database => new UnifiedConnectionEditorResult.Database(
                Database!.CreateSaveRequest()),
            _ => throw new ArgumentOutOfRangeException(nameof(Family), Family, null),
        };

    private void ApplyOption(UnifiedConnectionTypeOption option)
    {
        switch (option.Family)
        {
            case SavedConnectionFamily.Terminal when option.TerminalKind is { } kind:
                Terminal.Kind = kind;
                break;
            case SavedConnectionFamily.Files when Files is not null
                && option.FileKind is { } kind:
                Files.Kind = kind;
                break;
            case SavedConnectionFamily.Database when Database is not null
                && option.DatabaseDriverId is { } driverId:
                Database.SelectedDriver = Database.Drivers
                    .First(item => item.Id == driverId);
                break;
        }
    }

    private UnifiedConnectionTypeOption InitialOption(SavedConnectionFamily family)
    {
        var candidates = TypeOptions.Where(item => item.Family == family).ToArray();
        if (candidates.Length == 0)
        {
            return TypeOptions[0];
        }

        return family switch
        {
            SavedConnectionFamily.Terminal => candidates
                .FirstOrDefault(item => item.TerminalKind == Terminal.Kind)
                ?? candidates[0],
            SavedConnectionFamily.Files => candidates
                .FirstOrDefault(item => item.FileKind == Files!.Kind)
                ?? candidates[0],
            SavedConnectionFamily.Database => candidates
                .FirstOrDefault(item =>
                    item.DatabaseDriverId == Database!.SelectedDriver.Id)
                ?? candidates[0],
            _ => candidates[0],
        };
    }

    private IReadOnlyList<UnifiedConnectionTypeOption> BuildTypeOptions(
        SavedConnectionFamily? lockedFamily)
    {
        var options = new List<UnifiedConnectionTypeOption>();
        if (lockedFamily is null or SavedConnectionFamily.Terminal)
        {
            options.AddRange(
            [
                new(SavedConnectionFamily.Terminal, "Terminal", "Local", ConnectionKind.Local),
                new(SavedConnectionFamily.Terminal, "Terminal", "SSH", ConnectionKind.Ssh),
                new(SavedConnectionFamily.Terminal, "Terminal", "Docker", ConnectionKind.Docker),
                new(SavedConnectionFamily.Terminal, "Terminal", "WSL", ConnectionKind.Wsl),
            ]);
        }

        if (Files is not null && lockedFamily is null or SavedConnectionFamily.Files)
        {
            options.AddRange(
            [
                new(SavedConnectionFamily.Files, "Files", "Local folder", FileKind: FileProviderKind.Local),
                new(SavedConnectionFamily.Files, "Files", "SFTP", FileKind: FileProviderKind.Sftp),
                new(SavedConnectionFamily.Files, "Files", "FTP / FTPS", FileKind: FileProviderKind.Ftp),
                new(SavedConnectionFamily.Files, "Files", "S3", FileKind: FileProviderKind.S3),
                new(SavedConnectionFamily.Files, "Files", "SMB", FileKind: FileProviderKind.Smb),
                new(SavedConnectionFamily.Files, "Files", "WebDAV", FileKind: FileProviderKind.WebDav),
            ]);
        }

        if (Database is not null && lockedFamily is null or SavedConnectionFamily.Database)
        {
            options.AddRange(Database.Drivers.Select(driver =>
                new UnifiedConnectionTypeOption(
                    SavedConnectionFamily.Database,
                    "Database",
                    driver.DisplayName,
                    DatabaseDriverId: driver.Id)));
        }

        return options.AsReadOnly();
    }
}
