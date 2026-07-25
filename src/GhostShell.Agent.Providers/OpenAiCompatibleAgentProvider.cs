using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Providers;

internal sealed class OpenAiCompatibleAgentProvider(
    AiProviderProfile profile,
    string model,
    AiProviderHttpTransport transport,
    AiProviderRuntimeLimits limits) : IAgentProvider
{
    public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
        AgentProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(limits.StreamTimeout);
        await using var enumerator = StreamCoreAsync(request, operation.Token)
            .GetAsyncEnumerator(operation.Token);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
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

            if (!moved)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    private async IAsyncEnumerable<AgentProviderEvent> StreamCoreAsync(
        AgentProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = WriteRequest(request);
        using var httpRequest = await transport.CreateRequestAsync(
            profile,
            HttpMethod.Post,
            "chat/completions",
            "text/event-stream",
            body,
            cancellationToken).ConfigureAwait(false);
        using var response = await transport
            .SendAsync(profile, httpRequest, cancellationToken)
            .ConfigureAwait(false);
        AiProviderHttpTransport.ValidateContent(
            response,
            "text/event-stream",
            limits.MaximumStreamResponseBytes);
        yield return new AgentProviderEvent.ResponseStarted();

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var limited = new LimitedReadStream(
            responseStream,
            limits.MaximumStreamResponseBytes);
        var parser = SseParser.Create(
            limited,
            (_, data) =>
            {
                if (data.Length > limits.MaximumSseEventBytes)
                {
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.ResponseTooLarge);
                }

                return data.ToArray();
            });
        var state = new OpenAiStreamState(limits.MaximumProviderFragmentBytes);
        var eventCount = 0;
        await foreach (var item in parser
                           .EnumerateAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            eventCount = checked(eventCount + 1);
            if (eventCount > limits.MaximumSseEvents)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            if (item.Data.AsSpan().SequenceEqual("[DONE]"u8))
            {
                state.MarkDone();
                break;
            }

            using var document = AiProviderJson.Parse(item.Data);
            foreach (var providerEvent in state.Apply(document.RootElement))
            {
                yield return providerEvent;
            }
        }

        foreach (var providerEvent in state.Complete())
        {
            yield return providerEvent;
        }
    }

    private byte[] WriteRequest(AgentProviderRequest request) =>
        AiProviderJson.Write(
            limits.MaximumRequestBytes,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteBoolean("stream", true);
                writer.WriteStartArray("messages");
                foreach (var message in request.Messages)
                {
                    WriteMessage(writer, message);
                }

                writer.WriteEndArray();
                if (request.Tools.Length > 0)
                {
                    writer.WriteStartArray("tools");
                    foreach (var tool in request.Tools)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "function");
                        writer.WriteStartObject("function");
                        writer.WriteString("name", tool.ProviderName);
                        writer.WriteString("description", tool.Description);
                        writer.WritePropertyName("parameters");
                        tool.InputSchema.WriteTo(writer);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteString("tool_choice", "auto");
                }

                writer.WriteEndObject();
            });

    private static void WriteMessage(Utf8JsonWriter writer, AgentMessage message)
    {
        writer.WriteStartObject();
        switch (message.Role)
        {
            case AgentMessageRole.System:
            case AgentMessageRole.Summary:
                writer.WriteString("role", "system");
                writer.WriteString("content", message.Content);
                break;
            case AgentMessageRole.User:
                writer.WriteString("role", "user");
                writer.WriteString("content", message.Content);
                break;
            case AgentMessageRole.Assistant:
                writer.WriteString("role", "assistant");
                if (message.ToolCalls.Length == 0)
                {
                    writer.WriteString("content", message.Content);
                    break;
                }

                if (message.Content.Length == 0)
                {
                    writer.WriteNull("content");
                }
                else
                {
                    writer.WriteString("content", message.Content);
                }

                writer.WriteStartArray("tool_calls");
                foreach (var toolCall in message.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", toolCall.ProviderCallId);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString(
                        "name",
                        toolCall.ProviderName);
                    writer.WriteString(
                        "arguments",
                        toolCall.Arguments.GetRawText());
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case AgentMessageRole.Tool when message.ToolResult is { } result:
                writer.WriteString("role", "tool");
                writer.WriteString("tool_call_id", result.ProviderCallId);
                writer.WriteString(
                    "content",
                    AiProviderJson.ToolResultContent(result));
                break;
            default:
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        writer.WriteEndObject();
    }

    private sealed class OpenAiStreamState(int maximumFragmentBytes)
    {
        private readonly List<OpenAiToolCall> _toolCalls = [];
        private AgentProviderStopReason? _stopReason;
        private bool _done;

        public IReadOnlyList<AgentProviderEvent> Apply(JsonElement root)
        {
            if (_done
                || _stopReason is not null
                || root.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            var choices = AiProviderJson.RequiredArray(root, "choices");
            using var enumerator = choices.EnumerateArray();
            if (!enumerator.MoveNext())
            {
                throw ProtocolError();
            }

            var choice = enumerator.Current;
            if (enumerator.MoveNext() || choice.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            if (choice.TryGetProperty("index", out var choiceIndex)
                && (choiceIndex.ValueKind != JsonValueKind.Number
                    || !choiceIndex.TryGetInt32(out var parsedChoiceIndex)
                    || parsedChoiceIndex != 0))
            {
                throw ProtocolError();
            }

            var events = new List<AgentProviderEvent>();
            if (choice.TryGetProperty("delta", out var delta)
                && delta.ValueKind != JsonValueKind.Null)
            {
                if (delta.ValueKind != JsonValueKind.Object)
                {
                    throw ProtocolError();
                }

                AppendText(delta, "content", events);
                AppendText(delta, "refusal", events);
                if (delta.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind != JsonValueKind.Null)
                {
                    if (toolCalls.ValueKind != JsonValueKind.Array)
                    {
                        throw ProtocolError();
                    }

                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        ApplyToolCall(toolCall, events);
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finishReason)
                && finishReason.ValueKind != JsonValueKind.Null)
            {
                if (finishReason.ValueKind != JsonValueKind.String)
                {
                    throw ProtocolError();
                }

                var parsed = finishReason.GetString() switch
                {
                    "stop" => AgentProviderStopReason.EndTurn,
                    "tool_calls" => AgentProviderStopReason.ToolUse,
                    "length" => AgentProviderStopReason.MaximumTokens,
                    "content_filter" => AgentProviderStopReason.ContentFiltered,
                    _ => throw ProtocolError(),
                };
                if (_stopReason is not null && _stopReason != parsed)
                {
                    throw ProtocolError();
                }

                _stopReason = parsed;
            }

            return events;
        }

        public void MarkDone()
        {
            if (_done)
            {
                throw ProtocolError();
            }

            _done = true;
        }

        public IReadOnlyList<AgentProviderEvent> Complete()
        {
            if (!_done || _stopReason is null)
            {
                throw ProtocolError();
            }

            var events = new List<AgentProviderEvent>();
            foreach (var toolCall in _toolCalls)
            {
                toolCall.Complete(events);
            }

            events.Add(new AgentProviderEvent.ResponseCompleted(_stopReason.Value));
            return events;
        }

        private void AppendText(
            JsonElement delta,
            string propertyName,
            ICollection<AgentProviderEvent> events)
        {
            if (!delta.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = property.GetString()!;
            if (value.Length > 0)
            {
                ValidateFragment(value, maximumFragmentBytes);
                events.Add(new AgentProviderEvent.TextDelta(value));
            }
        }

        private void ApplyToolCall(
            JsonElement toolCall,
            ICollection<AgentProviderEvent> events)
        {
            if (toolCall.ValueKind != JsonValueKind.Object
                || !toolCall.TryGetProperty("index", out var indexProperty)
                || indexProperty.ValueKind != JsonValueKind.Number
                || !indexProperty.TryGetInt32(out var index)
                || index < 0
                || index > _toolCalls.Count)
            {
                throw ProtocolError();
            }

            if (index == _toolCalls.Count)
            {
                _toolCalls.Add(new OpenAiToolCall(index));
            }

            var state = _toolCalls[index];
            state.AcceptIdentity(
                AiProviderJson.OptionalBoundedString(toolCall, "id", 256),
                ReadFunctionString(toolCall, "name", 128));
            var arguments = ReadFunctionString(
                toolCall,
                "arguments",
                maximumFragmentBytes,
                allowControls: true);
            state.AcceptArguments(arguments, events);
        }

        private static string? ReadFunctionString(
            JsonElement toolCall,
            string propertyName,
            int maximumLength,
            bool allowControls = false)
        {
            if (!toolCall.TryGetProperty("function", out var function)
                || function.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (function.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            if (!function.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = property.GetString()!;
            if (Encoding.UTF8.GetByteCount(value) > maximumLength
                || (!allowControls && value.Any(char.IsControl)))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            return value;
        }

        private static void ValidateFragment(string value, int maximumBytes)
        {
            if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }
        }
    }

    private sealed class OpenAiToolCall(int index)
    {
        private string? _id;
        private string? _name;
        private bool _started;
        private bool _hasArguments;

        public void AcceptIdentity(string? id, string? name)
        {
            _id = MergeStable(_id, id);
            _name = MergeStable(_name, name);
        }

        public void AcceptArguments(
            string? arguments,
            ICollection<AgentProviderEvent> events)
        {
            if (arguments is null)
            {
                return;
            }

            EnsureStarted(events);
            if (arguments.Length > 0)
            {
                events.Add(new AgentProviderEvent.ToolCallArgumentsDelta(index, arguments));
                _hasArguments = true;
            }
        }

        public void Complete(ICollection<AgentProviderEvent> events)
        {
            EnsureStarted(events);
            if (!_hasArguments)
            {
                throw ProtocolError();
            }

            events.Add(new AgentProviderEvent.ToolCallCompleted(index));
        }

        private void EnsureStarted(ICollection<AgentProviderEvent> events)
        {
            if (_started)
            {
                return;
            }

            if (_id is null || _name is null)
            {
                throw ProtocolError();
            }

            events.Add(new AgentProviderEvent.ToolCallStarted(index, _id, _name));
            _started = true;
        }

        private static string? MergeStable(string? current, string? incoming)
        {
            if (incoming is null)
            {
                return current;
            }

            if (current is not null && !string.Equals(current, incoming, StringComparison.Ordinal))
            {
                throw ProtocolError();
            }

            return incoming;
        }
    }

    private static AiProviderClientException ProtocolError() =>
        AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
}
