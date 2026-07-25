using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Providers;

internal sealed class AiProviderModelDiscovery(
    AiProviderHttpTransport transport,
    AiProviderRuntimeLimits limits)
{
    public async ValueTask<IReadOnlyList<AiProviderModelDescriptor>> ListAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(limits.DiscoveryTimeout);
        try
        {
            using var request = await transport.CreateRequestAsync(
                profile,
                HttpMethod.Get,
                profile.ProviderKind == AiProviderKind.Anthropic
                    ? $"models?limit={limits.MaximumModels}"
                    : "models",
                "application/json",
                body: null,
                operation.Token).ConfigureAwait(false);
            using var response = await transport
                .SendAsync(profile, request, operation.Token)
                .ConfigureAwait(false);
            AiProviderHttpTransport.ValidateContent(
                response,
                "application/json",
                limits.MaximumModelResponseBytes);
            await using var responseStream = await response.Content
                .ReadAsStreamAsync(operation.Token)
                .ConfigureAwait(false);
            await using var limited = new LimitedReadStream(
                responseStream,
                limits.MaximumModelResponseBytes);
            using var document = await ParseAsync(limited, operation.Token).ConfigureAwait(false);
            return ParseModels(document.RootElement, limits.MaximumModels);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.Timeout,
                innerException: exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProviderUnavailable,
                innerException: exception);
        }
    }

    private static async ValueTask<JsonDocument> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(
                stream,
                AiProviderJson.DocumentOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError,
                innerException: exception);
        }
    }

    private static IReadOnlyList<AiProviderModelDescriptor> ParseModels(
        JsonElement root,
        int maximumModels)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        var data = AiProviderJson.RequiredArray(root, "data");
        var models = new List<AiProviderModelDescriptor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (models.Count == maximumModels)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError);
            }

            var id = AiProviderJson.RequiredBoundedString(
                item,
                "id",
                AiProviderProfile.MaximumModelIdLength);
            var displayName = AiProviderJson.OptionalBoundedString(
                    item,
                    "display_name",
                    AiProviderProfile.MaximumModelIdLength)
                ?? id;
            if (!ids.Add(id))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError);
            }

            try
            {
                models.Add(new AiProviderModelDescriptor(id, displayName));
            }
            catch (ArgumentException exception)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError,
                    innerException: exception);
            }
        }

        if (models.Count == 0)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ModelUnavailable);
        }

        return Array.AsReadOnly(models
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .ToArray());
    }
}
