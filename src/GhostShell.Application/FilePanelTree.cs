using System.Runtime.CompilerServices;

namespace GhostShell.Application;

/// <summary>
/// Walks a provider tree while hiding page size, continuation, and protocol differences. It is the
/// common discovery primitive behind search and observation; callers see every returned entry.
/// </summary>
internal static class FilePanelTree
{
    public static async IAsyncEnumerable<FilePanelResult<FilePanelEntry>> EnumerateAsync(
        IFilePanelClient client,
        FilePanelLocation root,
        FilePanelDiscoveryScope scope,
        bool showHidden,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(root);
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
        }

        var pageSize = client.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, root.ProviderProfileId, StringComparison.Ordinal))
            ?.MaximumPageSize ?? 250;
        var pending = new Stack<FilePanelLocation>();
        var visited = new HashSet<FilePanelLocation>();
        pending.Push(root.WithVersion(null));

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(directory))
            {
                continue;
            }

            var children = new List<FilePanelEntry>();
            string? continuation = null;
            var usedContinuations = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await client.ListAsync(
                    new FilePanelListRequest(
                        directory,
                        pageSize,
                        continuation,
                        showHidden),
                    cancellationToken).ConfigureAwait(false);
                if (!page.IsSuccess)
                {
                    yield return FilePanelResult<FilePanelEntry>.Failure(page.Error!);
                    yield break;
                }

                foreach (var entry in page.Value!.Entries)
                {
                    if (showHidden || !entry.IsHidden)
                    {
                        children.Add(entry);
                        yield return FilePanelResult<FilePanelEntry>.Success(entry);
                    }
                }

                continuation = page.Value.ContinuationToken;
                if (continuation is not null && !usedContinuations.Add(continuation))
                {
                    yield return InvalidContinuation();
                    yield break;
                }
            }
            while (continuation is not null);

            if (scope != FilePanelDiscoveryScope.Subtree)
            {
                continue;
            }

            foreach (var child in children
                .Where(entry => entry.Kind == FilePanelEntryKind.Directory)
                .Reverse())
            {
                pending.Push(child.Location.WithVersion(null));
            }
        }
    }

    private static FilePanelResult<FilePanelEntry> InvalidContinuation() =>
        FilePanelResult<FilePanelEntry>.Failure(new FilePanelError(
            FilePanelErrorCode.InvalidLocation,
            "file_list_continuation_cycle",
            "The file provider repeated a continuation token while traversing the location.",
            Retryable: false));
}
