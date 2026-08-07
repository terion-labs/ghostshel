using System.Collections.ObjectModel;

using GhostShell.Application;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Who can do what to one item, as something to look at and change.
///
/// Two shapes, one dialog. A filesystem answers with nine bits and the editor
/// shows a grid of checkboxes; an object store answers with a list of parties
/// and the editor shows one row each. Neither is converted into the other,
/// because the conversion would be a lie in both directions — an object store
/// has no group, and a filesystem has no notion of a named account.
/// </summary>
public sealed class FileAccessControlEditorViewModel : ObservableObject
{
    private readonly FilePanelAccessControl _original;
    private FilePanelPosixMode _mode;

    public FileAccessControlEditorViewModel(
        string itemName,
        string connectionName,
        FilePanelAccessControl accessControl,
        bool canEdit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(accessControl);
        _original = accessControl;
        _mode = accessControl.Mode ?? new FilePanelPosixMode(0);
        ItemName = itemName;
        CanEdit = canEdit;
        Summary = accessControl.Mode is not null
            ? $"Permission bits on {connectionName}"
            : $"Access control list on {connectionName}";
        Grants = [.. accessControl.Grants.Select(grant =>
            new FileAccessGrantViewModel(grant))];
    }

    public string ItemName { get; }

    public string Summary { get; }

    /// <summary>
    /// Whether this is a reading or an editing. A connection that grants the
    /// right to see its permissions need not grant the right to change them,
    /// and the refusal is better said here than after an Apply.
    /// </summary>
    public bool CanEdit { get; }

    public string ReadOnlyReason =>
        "This connection reports who has access but does not accept changes to it here.";

    public bool HasMode => _original.Mode is not null;

    public bool HasGrants => Grants.Count > 0;

    public ObservableCollection<FileAccessGrantViewModel> Grants { get; }

    public string ModeText => $"{_mode.Octal}  {_mode.Symbolic}";

    public bool OwnerRead
    {
        get => Has(FilePanelPosixWho.Owner, FilePanelPosixRight.Read);
        set => Set(FilePanelPosixWho.Owner, FilePanelPosixRight.Read, value);
    }

    public bool OwnerWrite
    {
        get => Has(FilePanelPosixWho.Owner, FilePanelPosixRight.Write);
        set => Set(FilePanelPosixWho.Owner, FilePanelPosixRight.Write, value);
    }

    public bool OwnerExecute
    {
        get => Has(FilePanelPosixWho.Owner, FilePanelPosixRight.Execute);
        set => Set(FilePanelPosixWho.Owner, FilePanelPosixRight.Execute, value);
    }

    public bool GroupRead
    {
        get => Has(FilePanelPosixWho.Group, FilePanelPosixRight.Read);
        set => Set(FilePanelPosixWho.Group, FilePanelPosixRight.Read, value);
    }

    public bool GroupWrite
    {
        get => Has(FilePanelPosixWho.Group, FilePanelPosixRight.Write);
        set => Set(FilePanelPosixWho.Group, FilePanelPosixRight.Write, value);
    }

    public bool GroupExecute
    {
        get => Has(FilePanelPosixWho.Group, FilePanelPosixRight.Execute);
        set => Set(FilePanelPosixWho.Group, FilePanelPosixRight.Execute, value);
    }

    public bool OtherRead
    {
        get => Has(FilePanelPosixWho.Other, FilePanelPosixRight.Read);
        set => Set(FilePanelPosixWho.Other, FilePanelPosixRight.Read, value);
    }

    public bool OtherWrite
    {
        get => Has(FilePanelPosixWho.Other, FilePanelPosixRight.Write);
        set => Set(FilePanelPosixWho.Other, FilePanelPosixRight.Write, value);
    }

    public bool OtherExecute
    {
        get => Has(FilePanelPosixWho.Other, FilePanelPosixRight.Execute);
        set => Set(FilePanelPosixWho.Other, FilePanelPosixRight.Execute, value);
    }

    /// <summary>
    /// The change to send, or null when nothing was changed — an Apply that
    /// alters nothing should not be a write at all, least of all one that
    /// bumps a version somebody else is holding.
    /// </summary>
    public FilePanelSetAccessControlRequest? BuildRequest()
    {
        if (!CanEdit)
        {
            return null;
        }

        if (_original.Mode is { } before)
        {
            return before.Permissions == _mode.Permissions
                ? null
                : new FilePanelSetAccessControlRequest(
                    _original.Location,
                    mode: _mode,
                    version: _original.Version);
        }

        var grants = Grants
            .Where(grant => grant.Rights != FilePanelAccessRight.None)
            .Select(grant => grant.ToGrant())
            .ToArray();
        var unchanged = grants.Length == _original.Grants.Count
            && grants.Zip(_original.Grants).All(pair =>
                pair.First.Grantee == pair.Second.Grantee
                && pair.First.Rights == pair.Second.Rights);
        return unchanged
            ? null
            : new FilePanelSetAccessControlRequest(
                _original.Location,
                grants: grants,
                version: _original.Version);
    }

    private bool Has(FilePanelPosixWho who, FilePanelPosixRight right) => _mode.Has(who, right);

    private void Set(FilePanelPosixWho who, FilePanelPosixRight right, bool granted)
    {
        if (Has(who, right) == granted)
        {
            return;
        }

        _mode = _mode.With(who, right, granted);
        OnPropertyChanged(PropertyName(who, right));
        OnPropertyChanged(nameof(ModeText));
    }

    private static string PropertyName(FilePanelPosixWho who, FilePanelPosixRight right) =>
        (who == FilePanelPosixWho.Other ? "Other" : who.ToString()) + right;
}

/// <summary>
/// One party and what it holds, as a row with a single choice. The service's
/// five permissions are offered as the four a person actually picks between:
/// nothing, read, read and write, everything.
/// </summary>
public sealed class FileAccessGrantViewModel : ObservableObject
{
    private static readonly (string Label, FilePanelAccessRight Rights)[] Options =
    [
        ("No access", FilePanelAccessRight.None),
        ("Read", FilePanelAccessRight.Read),
        ("Read and write", FilePanelAccessRight.Read | FilePanelAccessRight.Write),
        ("Full control", FilePanelAccessRight.FullControl),
    ];

    private readonly FilePanelGrantee _grantee;
    private string _permission;

    public FileAccessGrantViewModel(FilePanelAccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _grantee = grant.Grantee;
        Rights = grant.Rights;
        _permission = Nearest(grant.Rights);
    }

    public string Grantee => _grantee.Label;

    /// <summary>
    /// What the connection calls this party underneath the readable name, where
    /// the two differ — a canonical account id means nothing on its own, and
    /// leaving it out means two accounts named alike cannot be told apart.
    /// </summary>
    public string Detail
    {
        get
        {
            var detail = _grantee.DisplayName is not null && _grantee.Id is not null
                ? _grantee.Id
                : _grantee.Kind.ToString();
            // A well-known group is its own detail, and saying "Everyone"
            // twice on two lines reads as a rendering fault.
            return string.Equals(detail, Grantee, StringComparison.Ordinal)
                ? string.Empty
                : detail;
        }
    }

    public IReadOnlyList<string> Choices { get; } =
        Options.Select(option => option.Label).ToArray();

    public string Permission
    {
        get => _permission;
        set
        {
            if (SetProperty(ref _permission, value))
            {
                Rights = Options.FirstOrDefault(option => option.Label == value).Rights;
                OnPropertyChanged(nameof(PermissionLabel));
            }
        }
    }

    public string PermissionLabel => $"What {Grantee} is granted";

    public FilePanelAccessRight Rights { get; private set; }

    public FilePanelAccessGrant ToGrant() => new(_grantee, Rights);

    /// <summary>
    /// A grant the four choices cannot express exactly — read plus the right to
    /// change the list, say — shows as the smallest choice that covers it, so
    /// leaving the row alone never quietly takes something away.
    /// </summary>
    private static string Nearest(FilePanelAccessRight rights)
    {
        foreach (var option in Options)
        {
            if ((rights & ~option.Rights) == FilePanelAccessRight.None
                && option.Rights != FilePanelAccessRight.None)
            {
                return option.Label;
            }
        }

        return rights == FilePanelAccessRight.None
            ? Options[0].Label
            : Options[^1].Label;
    }
}
