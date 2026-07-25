using Amazon.S3;
using Amazon.S3.Model;

namespace GhostShell.Files;

/// <summary>Maps the narrow object-store seam to the maintained AWS SDK for .NET.</summary>
internal sealed class AwsS3ObjectStore(IAmazonS3 client) : IS3ObjectStore
{
    public async ValueTask<S3ObjectPage> ListAsync(
        string bucket,
        string prefix,
        int maximumItems,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    Delimiter = "/",
                    MaxKeys = maximumItems,
                    ContinuationToken = continuationToken,
                    Encoding = EncodingType.Url,
                },
                cancellationToken).ConfigureAwait(false);

            var objects = (response.S3Objects ?? [])
                .Select(value => new S3ObjectItem(
                    DecodeKey(value.Key),
                    value.Size ?? 0,
                    ToDateTimeOffset(value.LastModified),
                    value.ETag))
                .ToArray();
            var prefixes = (response.CommonPrefixes ?? [])
                .Select(DecodeKey)
                .ToArray();
            return new S3ObjectPage(
                objects,
                prefixes,
                response.IsTruncated ?? false,
                response.NextContinuationToken);
        }
        catch (AmazonS3Exception exception)
        {
            throw Wrap(exception);
        }
    }

    public async ValueTask<S3ObjectMetadata> HeadAsync(
        string bucket,
        string key,
        string? etagToMatch,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = key,
                    EtagToMatch = etagToMatch,
                },
                cancellationToken).ConfigureAwait(false);
            return new S3ObjectMetadata(
                response.ContentLength,
                ToDateTimeOffset(response.LastModified),
                response.ETag);
        }
        catch (AmazonS3Exception exception)
        {
            throw Wrap(exception);
        }
    }

    public async ValueTask<S3ObjectRead> ReadAsync(
        string bucket,
        string key,
        long start,
        long endInclusive,
        string etagToMatch,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ByteRange = new ByteRange(start, endInclusive),
                    EtagToMatch = etagToMatch,
                },
                cancellationToken).ConfigureAwait(false);
            return new S3ObjectRead(
                response.ResponseStream,
                response.ContentLength,
                response.ETag,
                new ResponseOwner(response));
        }
        catch (AmazonS3Exception exception)
        {
            throw Wrap(exception);
        }
    }

    public async ValueTask<S3ObjectMutation> WriteAsync(
        string bucket,
        string key,
        Stream source,
        long contentLength,
        string? ifMatch,
        string? ifNoneMatch,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = source,
                AutoCloseStream = false,
                AutoResetStreamPosition = false,
                IfMatch = ifMatch,
                IfNoneMatch = ifNoneMatch,
            };
            request.Headers.ContentLength = contentLength;
            var response = await client
                .PutObjectAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return new S3ObjectMutation(response.ETag, LastModifiedAt: null);
        }
        catch (AmazonS3Exception exception)
        {
            throw Wrap(exception);
        }
    }

    public async ValueTask<S3ObjectMutation> CopyAsync(
        string bucket,
        string sourceKey,
        string destinationKey,
        string sourceEtagToMatch,
        string? destinationIfMatch,
        string? destinationIfNoneMatch,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.CopyObjectAsync(
                new CopyObjectRequest
                {
                    SourceBucket = bucket,
                    SourceKey = sourceKey,
                    DestinationBucket = bucket,
                    DestinationKey = destinationKey,
                    ETagToMatch = sourceEtagToMatch,
                    IfMatch = destinationIfMatch,
                    IfNoneMatch = destinationIfNoneMatch,
                },
                cancellationToken).ConfigureAwait(false);
            return new S3ObjectMutation(
                response.ETag,
                ToDateTimeOffset(response.LastModified));
        }
        catch (AmazonS3Exception exception)
        {
            throw Wrap(exception);
        }
    }

    public async ValueTask DeleteAsync(
        string bucket,
        string key,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        try
        {
            // SocketsHttpHandler can transparently replay DELETE after a response-less
            // disconnect. A one-key multi-delete is the equivalent S3 operation over POST, so
            // the governed mutation retains its single-dispatch boundary below the SDK.
            var response = await client.DeleteObjectsAsync(
                new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects =
                    [
                        new KeyVersion
                        {
                            Key = key,
                            ETag = ifMatch,
                            // GhostShell versions are ETags, not S3 bucket version IDs. Leaving
                            // this null preserves DeleteObject's current-version semantics.
                            VersionId = null,
                        },
                    ],
                    Quiet = true,
                },
                cancellationToken).ConfigureAwait(false);

            if (response.DeleteErrors?.FirstOrDefault() is { } error)
            {
                throw FromDeleteError(
                    error,
                    new InvalidDataException("S3 returned a per-object delete error."));
            }
        }
        catch (DeleteObjectsException exception)
        {
            if (exception.Response.DeleteErrors?.FirstOrDefault() is not { } error)
            {
                throw Wrap(exception);
            }

            throw FromDeleteError(error, exception);
        }
        catch (AmazonS3Exception exception)
        {
            throw Wrap(exception);
        }
    }

    private static string DecodeKey(string encoded) => Uri.UnescapeDataString(encoded);

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value) =>
        value is { } dateTime
            ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc))
            : null;

    private static DateTimeOffset? ToDateTimeOffset(string? value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
                ? parsed
                : null;

    private static S3StoreException Wrap(AmazonS3Exception exception) =>
        new(
            exception.StatusCode,
            exception.ErrorCode,
            exception.Message,
            exception);

    private static S3StoreException FromDeleteError(DeleteError error, Exception innerException) =>
        new(
            error.Code switch
            {
                "PreconditionFailed" => System.Net.HttpStatusCode.PreconditionFailed,
                "NoSuchKey" or "NoSuchVersion" or "NotFound" =>
                    System.Net.HttpStatusCode.NotFound,
                "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" =>
                    System.Net.HttpStatusCode.Forbidden,
                "InvalidArgument" or "InvalidRequest" or "MalformedXML" =>
                    System.Net.HttpStatusCode.BadRequest,
                "OperationAborted" => System.Net.HttpStatusCode.Conflict,
                "SlowDown" or "ServiceUnavailable" =>
                    System.Net.HttpStatusCode.ServiceUnavailable,
                _ => System.Net.HttpStatusCode.InternalServerError,
            },
            error.Code,
            "The S3 service rejected the object delete.",
            innerException);

    private sealed class ResponseOwner(GetObjectResponse response) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
