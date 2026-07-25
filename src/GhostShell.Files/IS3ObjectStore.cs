namespace GhostShell.Files;

/// <summary>
/// Narrow seam over the AWS SDK. It keeps SDK transport types out of provider semantics and lets
/// deterministic tests model S3 responses without credentials or a live endpoint.
/// </summary>
internal interface IS3ObjectStore
{
    ValueTask<S3ObjectPage> ListAsync(
        string bucket,
        string prefix,
        int maximumItems,
        string? continuationToken,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectMetadata> HeadAsync(
        string bucket,
        string key,
        string? etagToMatch,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectRead> ReadAsync(
        string bucket,
        string key,
        long start,
        long endInclusive,
        string etagToMatch,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectMutation> WriteAsync(
        string bucket,
        string key,
        Stream source,
        long contentLength,
        string? ifMatch,
        string? ifNoneMatch,
        CancellationToken cancellationToken);

    ValueTask<S3ObjectMutation> CopyAsync(
        string bucket,
        string sourceKey,
        string destinationKey,
        string sourceEtagToMatch,
        string? destinationIfMatch,
        string? destinationIfNoneMatch,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        string bucket,
        string key,
        string? ifMatch,
        CancellationToken cancellationToken);
}

internal sealed record S3ObjectPage(
    IReadOnlyList<S3ObjectItem> Objects,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextContinuationToken);

internal sealed record S3ObjectItem(
    string Key,
    long Size,
    DateTimeOffset? LastModifiedAt,
    string ETag);

internal sealed record S3ObjectMetadata(
    long Size,
    DateTimeOffset? LastModifiedAt,
    string ETag);

internal sealed record S3ObjectMutation(
    string ETag,
    DateTimeOffset? LastModifiedAt);

internal sealed class S3ObjectRead(
    Stream content,
    long contentLength,
    string etag,
    IAsyncDisposable owner) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public long ContentLength { get; } = contentLength;

    public string ETag { get; } = etag;

    public ValueTask DisposeAsync() => owner.DisposeAsync();
}
