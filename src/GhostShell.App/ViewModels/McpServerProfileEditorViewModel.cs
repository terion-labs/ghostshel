using System.Collections.ObjectModel;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed record McpServerTrustReviewEntry(
    string Label,
    string Value);

public enum McpServerCredentialReviewState
{
    Available,
    Missing,
    WrongScope,
}

public sealed record McpServerCredentialTrustReviewEntry(
    string VariableName,
    SecretRef Reference,
    string CredentialLabel,
    string CredentialKind,
    McpServerCredentialReviewState State)
{
    public string ReferenceValue => Reference.Value;

    public string MetadataSummary => State == McpServerCredentialReviewState.Missing
        ? "Credential metadata not found"
        : $"{CredentialLabel} · {CredentialKind}";

    public string StateSummary => State switch
    {
        McpServerCredentialReviewState.Available =>
            "Available to this MCP server profile",
        McpServerCredentialReviewState.Missing =>
            "Missing credential",
        McpServerCredentialReviewState.WrongScope =>
            "Wrong scope · unavailable to this MCP server profile",
        _ => throw new ArgumentOutOfRangeException(
            nameof(State),
            State,
            null),
    };

    public bool HasWarning => State != McpServerCredentialReviewState.Available;

    public string AutomationName =>
        $"{VariableName} vault binding: {MetadataSummary}; {StateSummary}";
}

public sealed record McpServerTrustReview(
    string ServerName,
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Changes,
    IReadOnlyList<McpServerTrustReviewEntry> Arguments,
    IReadOnlyList<McpServerCredentialTrustReviewEntry> Environment,
    IReadOnlyList<string> EnabledTools)
{
    public bool HasArguments => Arguments.Count > 0;

    public bool HasNoArguments => !HasArguments;

    public bool HasEnvironment => Environment.Count > 0;

    public bool HasNoEnvironment => !HasEnvironment;

    public bool HasEnabledTools => EnabledTools.Count > 0;

    public bool HasNoEnabledTools => !HasEnabledTools;
}

/// <summary>
/// Carries the immutable profile reviewed by the user. Production callers
/// cannot construct or confirm this receipt outside the App assembly.
/// </summary>
public sealed record McpServerProfileSaveRequest
{
    internal McpServerProfileSaveRequest(
        McpServerProfile profile,
        long? expectedRevision,
        bool requiresTrustConfirmation,
        bool isTrustConfirmed,
        McpServerTrustReview trustReview)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ExpectedRevision = expectedRevision;
        RequiresTrustConfirmation = requiresTrustConfirmation;
        IsTrustConfirmed = isTrustConfirmed;
        TrustReview = trustReview ?? throw new ArgumentNullException(nameof(trustReview));
    }

    public McpServerProfile Profile { get; }

    public long? ExpectedRevision { get; }

    public bool RequiresTrustConfirmation { get; }

    public bool IsTrustConfirmed { get; }

    public McpServerTrustReview TrustReview { get; }

    public bool IsAuthorizedForSave =>
        !RequiresTrustConfirmation || IsTrustConfirmed;

    internal McpServerProfileSaveRequest ConfirmTrust() =>
        new(
            Profile,
            ExpectedRevision,
            RequiresTrustConfirmation,
            isTrustConfirmed: true,
            TrustReview);
}

public sealed class McpArgumentEditorItemViewModel : ObservableObject
{
    private int _position;
    private string _value;

    internal McpArgumentEditorItemViewModel(int position, string value)
    {
        _position = position;
        _value = value;
    }

    public int Position
    {
        get => _position;
        internal set => SetProperty(ref _position, value);
    }

    public string AccessibleName => $"Argument {Position}";

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    internal void NotifyPositionChanged()
    {
        OnPropertyChanged(nameof(AccessibleName));
    }
}

public sealed class McpEnvironmentBindingEditorItemViewModel : ObservableObject
{
    private int _position;
    private string _name;
    private string _secretReference;

    internal McpEnvironmentBindingEditorItemViewModel(
        int position,
        string name,
        string secretReference)
    {
        _position = position;
        _name = name;
        _secretReference = secretReference;
    }

    public int Position => _position;

    public string NameAccessibleName =>
        $"Environment binding {Position} variable name";

    public string SecretReferenceAccessibleName =>
        $"Environment binding {Position} secret reference";

    public string RemoveAccessibleName =>
        $"Remove environment binding {Position}";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string SecretReference
    {
        get => _secretReference;
        set => SetProperty(ref _secretReference, value ?? string.Empty);
    }

    internal void UpdatePosition(int position)
    {
        if (!SetProperty(ref _position, position, nameof(Position)))
        {
            return;
        }

        OnPropertyChanged(nameof(NameAccessibleName));
        OnPropertyChanged(nameof(SecretReferenceAccessibleName));
        OnPropertyChanged(nameof(RemoveAccessibleName));
    }
}

public sealed class McpEnabledToolEditorItemViewModel : ObservableObject
{
    private int _position;
    private string _name;

    internal McpEnabledToolEditorItemViewModel(int position, string name)
    {
        _position = position;
        _name = name;
    }

    public int Position => _position;

    public string NameAccessibleName => $"Enabled MCP tool {Position} name";

    public string RemoveAccessibleName => $"Remove enabled MCP tool {Position}";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    internal void UpdatePosition(int position)
    {
        if (!SetProperty(ref _position, position, nameof(Position)))
        {
            return;
        }

        OnPropertyChanged(nameof(NameAccessibleName));
        OnPropertyChanged(nameof(RemoveAccessibleName));
    }
}

/// <summary>
/// Edits one direct stdio MCP process definition. Command, argv, and vault
/// references stay structurally separate so the UI cannot imply shell parsing.
/// </summary>
public sealed class McpServerProfileEditorViewModel : ObservableObject
{
    private readonly McpServerProfileId _id;
    private readonly int _schemaVersion;
    private readonly McpServerProfile? _original;
    private readonly IReadOnlyList<SecretMetadataViewModel> _secrets;
    private string _name = string.Empty;
    private string _executable = string.Empty;
    private string _workingDirectory = string.Empty;
    private bool _isEnabled = true;

    public McpServerProfileEditorViewModel(
        McpServerProfile? existing = null,
        long? expectedRevision = null,
        IReadOnlyList<SecretMetadataViewModel>? secrets = null)
    {
        if (existing is null && expectedRevision is not null)
        {
            throw new ArgumentException(
                "A revision can be supplied only when editing an MCP server.",
                nameof(expectedRevision));
        }

        _original = existing;
        _secrets = secrets?.ToArray() ?? [];
        _id = existing?.Id ?? McpServerProfileId.New();
        _schemaVersion = existing?.SchemaVersion ?? McpServerProfile.CurrentSchemaVersion;
        ExpectedRevision = expectedRevision;
        if (existing is null)
        {
            return;
        }

        _name = existing.Name;
        _executable = existing.Executable;
        _workingDirectory = existing.WorkingDirectory ?? string.Empty;
        _isEnabled = existing.IsEnabled;
        foreach (var argument in existing.Arguments)
        {
            Arguments.Add(new McpArgumentEditorItemViewModel(
                Arguments.Count + 1,
                argument));
        }

        foreach (var binding in existing.Environment)
        {
            Environment.Add(new McpEnvironmentBindingEditorItemViewModel(
                Environment.Count + 1,
                binding.Name,
                binding.Reference.Value));
        }

        foreach (var tool in existing.EnabledTools)
        {
            EnabledTools.Add(new McpEnabledToolEditorItemViewModel(
                EnabledTools.Count + 1,
                tool));
        }
    }

    public long? ExpectedRevision { get; }

    public bool IsEditing => ExpectedRevision is not null;

    public string EditorTitle => IsEditing ? "Edit MCP server" : "New MCP server";

    public string ProfileId => _id.Value;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Executable
    {
        get => _executable;
        set => SetProperty(ref _executable, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public ObservableCollection<McpArgumentEditorItemViewModel> Arguments { get; } = [];

    public ObservableCollection<McpEnvironmentBindingEditorItemViewModel> Environment { get; } = [];

    public ObservableCollection<McpEnabledToolEditorItemViewModel> EnabledTools { get; } = [];

    public void AddArgument()
    {
        EnsureRoom(Arguments.Count, McpServerProfile.MaximumArgumentCount, "arguments");
        Arguments.Add(new McpArgumentEditorItemViewModel(Arguments.Count + 1, string.Empty));
    }

    public void RemoveArgument(McpArgumentEditorItemViewModel argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (Arguments.Remove(argument))
        {
            RefreshArgumentPositions();
        }
    }

    public void MoveArgumentUp(McpArgumentEditorItemViewModel argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var index = Arguments.IndexOf(argument);
        if (index > 0)
        {
            Arguments.Move(index, index - 1);
            RefreshArgumentPositions();
        }
    }

    public void MoveArgumentDown(McpArgumentEditorItemViewModel argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var index = Arguments.IndexOf(argument);
        if (index >= 0 && index < Arguments.Count - 1)
        {
            Arguments.Move(index, index + 1);
            RefreshArgumentPositions();
        }
    }

    public void AddEnvironmentBinding()
    {
        EnsureRoom(
            Environment.Count,
            McpServerProfile.MaximumEnvironmentVariableCount,
            "environment bindings");
        Environment.Add(new McpEnvironmentBindingEditorItemViewModel(
            Environment.Count + 1,
            NextEnvironmentName(),
            SecretRef.New().Value));
    }

    public void RemoveEnvironmentBinding(McpEnvironmentBindingEditorItemViewModel binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (Environment.Remove(binding))
        {
            RefreshEnvironmentPositions();
        }
    }

    public void AddEnabledTool()
    {
        EnsureRoom(
            EnabledTools.Count,
            McpServerProfile.MaximumEnabledToolCount,
            "enabled tools");
        EnabledTools.Add(new McpEnabledToolEditorItemViewModel(
            EnabledTools.Count + 1,
            string.Empty));
    }

    public void RemoveEnabledTool(McpEnabledToolEditorItemViewModel tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (EnabledTools.Remove(tool))
        {
            RefreshEnabledToolPositions();
        }
    }

    public McpServerProfileSaveRequest CreateSaveRequest()
    {
        var executable = Required(Executable, "Executable");
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException(
                "Executable must be a fully qualified local path.");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
            ? null
            : Required(WorkingDirectory, "Working directory");
        if (workingDirectory is not null
            && !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException(
                "Working directory must be a fully qualified local path.");
        }

        var profile = new McpServerProfile(
            _id,
            _schemaVersion,
            Required(Name, "Server name"),
            executable,
            Arguments.Select(argument => argument.Value).ToArray(),
            workingDirectory,
            Environment.Select(binding => new McpServerEnvironmentVariable(
                Required(binding.Name, "Environment variable name"),
                new SecretRef(Required(
                    binding.SecretReference,
                    "Environment secret reference"))))
                .ToArray(),
            EnabledTools.Select(tool => Required(tool.Name, "Enabled tool name"))
                .ToArray(),
            IsEnabled);
        var review = CreateTrustReview(profile);
        return new McpServerProfileSaveRequest(
            profile,
            ExpectedRevision,
            review.Changes.Count > 0,
            isTrustConfirmed: false,
            review);
    }

    private McpServerTrustReview CreateTrustReview(McpServerProfile profile)
    {
        var changes = new List<string>();
        if (_original is null)
        {
            changes.Add("Add a new local MCP server process");
        }
        else
        {
            if (!string.Equals(
                    profile.Executable,
                    _original.Executable,
                    StringComparison.Ordinal))
            {
                changes.Add("Change the executable");
            }

            if (!profile.Arguments.SequenceEqual(
                    _original.Arguments,
                    StringComparer.Ordinal))
            {
                changes.Add("Change the ordered argument list");
            }

            if (!string.Equals(
                    profile.WorkingDirectory,
                    _original.WorkingDirectory,
                    StringComparison.Ordinal))
            {
                changes.Add("Change the working directory");
            }

            if (!profile.Environment.SequenceEqual(_original.Environment))
            {
                changes.Add("Change environment-to-vault bindings");
            }

            if (profile.IsEnabled && !_original.IsEnabled)
            {
                changes.Add("Enable this server for future governed agent runs");
            }

            var addedTools = profile.EnabledTools
                .Except(_original.EnabledTools, StringComparer.Ordinal)
                .ToArray();
            if (addedTools.Length > 0)
            {
                changes.Add(addedTools.Length == 1
                    ? $"Enable MCP tool “{addedTools[0]}”"
                    : $"Enable {addedTools.Length} additional MCP tools");
            }
        }

        return new McpServerTrustReview(
            profile.Name,
            profile.Executable,
            profile.WorkingDirectory ?? "Executable directory",
            changes.AsReadOnly(),
            profile.Arguments
                .Select((argument, index) => new McpServerTrustReviewEntry(
                    $"Argument {index + 1}",
                    argument.Length == 0 ? "(empty argument)" : argument))
                .ToArray(),
            profile.Environment
                .Select(binding => CreateCredentialTrustReviewEntry(
                    profile.Id,
                    binding))
                .ToArray(),
            profile.EnabledTools.ToArray());
    }

    private McpServerCredentialTrustReviewEntry
        CreateCredentialTrustReviewEntry(
            McpServerProfileId profileId,
            McpServerEnvironmentVariable binding)
    {
        var matchingReference = _secrets
            .Where(secret => secret.Reference == binding.Reference)
            .ToArray();
        var available = matchingReference.FirstOrDefault(secret =>
            secret.SecretScope.Kind == SecretScopeKind.McpServer
            && string.Equals(
                secret.SecretScope.OwnerId,
                profileId.Value,
                StringComparison.Ordinal));
        if (available is not null)
        {
            return new McpServerCredentialTrustReviewEntry(
                binding.Name,
                binding.Reference,
                available.Label,
                available.Kind,
                McpServerCredentialReviewState.Available);
        }

        var wrongScope = matchingReference.FirstOrDefault();
        return wrongScope is null
            ? new McpServerCredentialTrustReviewEntry(
                binding.Name,
                binding.Reference,
                "Not found",
                "UNAVAILABLE",
                McpServerCredentialReviewState.Missing)
            : new McpServerCredentialTrustReviewEntry(
                binding.Name,
                binding.Reference,
                wrongScope.Label,
                wrongScope.Kind,
                McpServerCredentialReviewState.WrongScope);
    }

    private string NextEnvironmentName()
    {
        const string baseName = "MCP_SECRET";
        var existing = Environment
            .Select(binding => binding.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; suffix <= McpServerProfile.MaximumEnvironmentVariableCount; suffix++)
        {
            var candidate = $"{baseName}_{suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No environment binding name is available.");
    }

    private void RefreshArgumentPositions()
    {
        for (var index = 0; index < Arguments.Count; index++)
        {
            Arguments[index].Position = index + 1;
            Arguments[index].NotifyPositionChanged();
        }
    }

    private void RefreshEnvironmentPositions()
    {
        for (var index = 0; index < Environment.Count; index++)
        {
            Environment[index].UpdatePosition(index + 1);
        }
    }

    private void RefreshEnabledToolPositions()
    {
        for (var index = 0; index < EnabledTools.Count; index++)
        {
            EnabledTools[index].UpdatePosition(index + 1);
        }
    }

    private static void EnsureRoom(int count, int maximum, string itemName)
    {
        if (count >= maximum)
        {
            throw new InvalidOperationException(
                $"An MCP server cannot define more than {maximum} {itemName}.");
        }
    }

    private static string Required(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{label} is required.");
        }

        return value.Trim();
    }
}
