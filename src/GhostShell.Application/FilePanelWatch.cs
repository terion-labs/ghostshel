using System.Runtime.CompilerServices;

namespace GhostShell.Application;

public static class FilePanelWatch
{
    public static async IAsyncEnumerable<FilePanelResult<FilePanelChange>> ObserveAsync(
        IFilePanelClient client,
        FilePanelWatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        var baseline = await CaptureAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess)
        {
            yield return FilePanelResult<FilePanelChange>.Failure(baseline.Error!);
            yield break;
        }

        yield return Changed(request, FilePanelChangeKind.Synchronized);
        while (true)
        {
            await Task.Delay(request.Interval, cancellationToken).ConfigureAwait(false);
            var current = await CaptureAsync(client, request, cancellationToken).ConfigureAwait(false);
            if (!current.IsSuccess)
            {
                yield return FilePanelResult<FilePanelChange>.Failure(current.Error!);
                if (current.Error?.Retryable != true)
                {
                    yield break;
                }

                continue;
            }

            if (!baseline.Value!.SequenceEqual(current.Value!))
            {
                baseline = current;
                yield return Changed(request, FilePanelChangeKind.Changed);
            }
        }
    }

    private static async ValueTask<FilePanelResult<IReadOnlyList<FilePanelEntrySignature>>>
        CaptureAsync(
            IFilePanelClient client,
            FilePanelWatchRequest request,
            CancellationToken cancellationToken)
    {
        var entries = new List<FilePanelEntrySignature>();
        await foreach (var result in FilePanelTree.EnumerateAsync(
            client,
            request.Location,
            request.Scope,
            request.ShowHidden,
            cancellationToken).ConfigureAwait(false))
        {
            if (!result.IsSuccess)
            {
                return FilePanelResult<IReadOnlyList<FilePanelEntrySignature>>.Failure(
                    result.Error!);
            }

            entries.Add(FilePanelEntrySignature.From(result.Value!));
        }

        entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Identity, right.Identity));
        return FilePanelResult<IReadOnlyList<FilePanelEntrySignature>>.Success(entries);
    }

    private static FilePanelResult<FilePanelChange> Changed(
        FilePanelWatchRequest request,
        FilePanelChangeKind kind) =>
        FilePanelResult<FilePanelChange>.Success(new FilePanelChange(request.Location, kind));

    private sealed record FilePanelEntrySignature(
        string Identity,
        FilePanelEntryKind Kind,
        long? Size,
        DateTimeOffset? LastModifiedAt,
        bool IsHidden)
    {
        public static FilePanelEntrySignature From(FilePanelEntry entry) => new(
            entry.Location.ToString(),
            entry.Kind,
            entry.Size,
            entry.LastModifiedAt,
            entry.IsHidden);
    }
}
