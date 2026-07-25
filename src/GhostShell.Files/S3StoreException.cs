using System.Net;

namespace GhostShell.Files;

internal sealed class S3StoreException(
    HttpStatusCode statusCode,
    string? serviceCode,
    string message,
    Exception innerException) : Exception(message, innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? ServiceCode { get; } = serviceCode;
}
