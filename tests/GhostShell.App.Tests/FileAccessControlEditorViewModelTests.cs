using GhostShell.App.ViewModels;
using GhostShell.Application;

namespace GhostShell.App.Tests;

/// <summary>
/// The permissions dialog. Two shapes share it, and the one thing both must get
/// right is that closing it without changing anything is not a write: a write
/// bumps a version somebody else may be holding, and on an object store it
/// replaces the whole list.
/// </summary>
public sealed class FileAccessControlEditorViewModelTests
{
    private static readonly FilePanelLocation Location = new(
        "files.remote",
        "example",
        new FilePanelAddress.Hierarchical(
            FilePanelPath.Root.Append(new FilePanelPathSegment("script.sh"))));

    private static FileAccessControlEditorViewModel Posix(int mode, bool canEdit = true) =>
        new(
            "script.sh",
            "example",
            new FilePanelAccessControl(Location, new FilePanelPosixMode(mode)),
            canEdit);

    [Fact]
    public void A_filesystem_shows_its_bits_and_not_a_list_of_grants()
    {
        var editor = Posix(0b110_100_100);

        Assert.True(editor.HasMode);
        Assert.False(editor.HasGrants);
        Assert.True(editor.OwnerRead);
        Assert.True(editor.OwnerWrite);
        Assert.False(editor.OwnerExecute);
        Assert.True(editor.GroupRead);
        Assert.False(editor.GroupWrite);
        Assert.False(editor.OtherWrite);
        Assert.Contains("644", editor.ModeText, StringComparison.Ordinal);
    }

    [Fact]
    public void Closing_without_changing_anything_is_not_a_write()
    {
        var editor = Posix(0b110_100_100);

        Assert.Null(editor.BuildRequest());

        // And setting a bit back the way it was is still nothing.
        editor.OwnerRead = false;
        editor.OwnerRead = true;

        Assert.Null(editor.BuildRequest());
    }

    [Fact]
    public void A_changed_bit_is_sent_as_the_whole_mode()
    {
        var editor = Posix(0b110_100_100);

        editor.OwnerExecute = true;
        editor.GroupExecute = true;
        editor.OtherExecute = true;

        var request = editor.BuildRequest();

        Assert.NotNull(request);
        Assert.Equal("755", request!.Mode!.Octal);
        Assert.Null(request.Grants);
        Assert.Equal(Location, request.Location);
    }

    /// <summary>
    /// A connection that reports access without accepting changes gets a
    /// dialog that says so, and cannot be made to send one anyway.
    /// </summary>
    [Fact]
    public void A_reading_that_cannot_be_changed_never_becomes_a_write()
    {
        var editor = Posix(0b110_100_100, canEdit: false);

        editor.OwnerExecute = true;

        Assert.False(editor.CanEdit);
        Assert.Null(editor.BuildRequest());
    }

    private static FileAccessControlEditorViewModel ObjectStore(
        params FilePanelAccessGrant[] grants) =>
        new(
            "report.csv",
            "bucket",
            new FilePanelAccessControl(Location, grants: grants),
            canEdit: true);

    [Fact]
    public void An_object_store_shows_one_row_per_party_and_no_bits()
    {
        var editor = ObjectStore(
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.Everyone),
                FilePanelAccessRight.Read),
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.User, "p3179430"),
                FilePanelAccessRight.FullControl));

        Assert.False(editor.HasMode);
        Assert.True(editor.HasGrants);
        Assert.Equal(["Everyone", "p3179430"], editor.Grants.Select(grant => grant.Grantee));
        Assert.Equal("Read", editor.Grants[0].Permission);
        Assert.Equal("Full control", editor.Grants[1].Permission);
        Assert.Null(editor.BuildRequest());
    }

    [Fact]
    public void Changing_what_a_party_holds_sends_the_whole_list_back()
    {
        var editor = ObjectStore(
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.Everyone),
                FilePanelAccessRight.Read),
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.User, "p3179430"),
                FilePanelAccessRight.FullControl));

        editor.Grants[0].Permission = "No access";

        var request = editor.BuildRequest();

        // The whole list, minus the party that now holds nothing — which is how
        // the service is told to drop it, there being no way to remove one.
        Assert.NotNull(request);
        Assert.Null(request!.Mode);
        var grant = Assert.Single(request.Grants!);
        Assert.Equal("p3179430", grant.Grantee.Id);
        Assert.Equal(FilePanelAccessRight.FullControl, grant.Rights);
    }

    /// <summary>
    /// A grant the four choices cannot express exactly — read plus the right to
    /// change the list — shows as the smallest choice that covers it, and keeps
    /// the grant it actually has until somebody picks something else. Leaving a
    /// row alone must never quietly take a permission away.
    /// </summary>
    [Fact]
    public void A_grant_the_choices_cannot_express_is_shown_rounded_up_and_kept_exactly()
    {
        var exact = FilePanelAccessRight.Read | FilePanelAccessRight.ReadAcl;
        var editor = ObjectStore(
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.User, "auditor"),
                exact));

        Assert.Equal("Full control", editor.Grants[0].Permission);
        Assert.Equal(exact, editor.Grants[0].Rights);
        Assert.Null(editor.BuildRequest());

        // And picking something is what changes it.
        editor.Grants[0].Permission = "Read";

        Assert.Equal(FilePanelAccessRight.Read, editor.Grants[0].Rights);
        Assert.Equal(
            FilePanelAccessRight.Read,
            Assert.Single(editor.BuildRequest()!.Grants!).Rights);
    }
}
