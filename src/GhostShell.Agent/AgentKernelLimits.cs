namespace GhostShell.Agent;

public sealed class AgentKernelLimits
{
    public AgentKernelLimits(
        int maximumProviderTextFragmentBytes = 8 * 1024,
        int maximumAssistantTextBytes = 256 * 1024,
        int maximumProviderEventsPerTurn = 4 * 1024,
        int maximumConcurrentProviderOperations = 2,
        int maximumToolCallsPerTurn = 16,
        int maximumToolArgumentFragmentBytes = 8 * 1024,
        int maximumToolArgumentBytes = 64 * 1024,
        int maximumTotalToolArgumentBytesPerTurn = 4 * 1024 * 1024,
        int maximumJsonDepth = 32,
        int maximumJsonNodes = 4 * 1024,
        int maximumConversationMessages = 256,
        int maximumConversationBytes = 2 * 1024 * 1024,
        int maximumRetainedEvents = 512,
        int maximumEventBatchSize = 64,
        int maximumToolResultBytes = 64 * 1024,
        int maximumTotalToolResultBytesPerTurn = 1024 * 1024,
        int maximumToolDefinitions = 128,
        int maximumToolSchemaBytes = 64 * 1024,
        int maximumTotalToolSchemaBytes = 512 * 1024)
    {
        MaximumProviderTextFragmentBytes = RequireInRange(
            maximumProviderTextFragmentBytes,
            1,
            64 * 1024,
            nameof(maximumProviderTextFragmentBytes));
        MaximumAssistantTextBytes = RequireInRange(
            maximumAssistantTextBytes,
            MaximumProviderTextFragmentBytes,
            2 * 1024 * 1024,
            nameof(maximumAssistantTextBytes));
        MaximumProviderEventsPerTurn = RequireInRange(
            maximumProviderEventsPerTurn,
            2,
            64 * 1024,
            nameof(maximumProviderEventsPerTurn));
        MaximumConcurrentProviderOperations = RequireInRange(
            maximumConcurrentProviderOperations,
            1,
            4,
            nameof(maximumConcurrentProviderOperations));
        MaximumToolDefinitions = RequireInRange(
            maximumToolDefinitions,
            1,
            128,
            nameof(maximumToolDefinitions));
        MaximumToolSchemaBytes = RequireInRange(
            maximumToolSchemaBytes,
            1,
            1024 * 1024,
            nameof(maximumToolSchemaBytes));
        MaximumTotalToolSchemaBytes = RequireInRange(
            maximumTotalToolSchemaBytes,
            MaximumToolSchemaBytes,
            16 * 1024 * 1024,
            nameof(maximumTotalToolSchemaBytes));
        MaximumToolCallsPerTurn = RequireInRange(
            maximumToolCallsPerTurn,
            1,
            128,
            nameof(maximumToolCallsPerTurn));
        MaximumToolArgumentFragmentBytes = RequireInRange(
            maximumToolArgumentFragmentBytes,
            1,
            64 * 1024,
            nameof(maximumToolArgumentFragmentBytes));
        MaximumToolArgumentBytes = RequireInRange(
            maximumToolArgumentBytes,
            MaximumToolArgumentFragmentBytes,
            1024 * 1024,
            nameof(maximumToolArgumentBytes));
        MaximumTotalToolArgumentBytesPerTurn = RequireInRange(
            maximumTotalToolArgumentBytesPerTurn,
            MaximumToolArgumentBytes,
            16 * 1024 * 1024,
            nameof(maximumTotalToolArgumentBytesPerTurn));
        MaximumJsonDepth = RequireInRange(
            maximumJsonDepth,
            1,
            128,
            nameof(maximumJsonDepth));
        MaximumJsonNodes = RequireInRange(
            maximumJsonNodes,
            1,
            64 * 1024,
            nameof(maximumJsonNodes));
        MaximumConversationMessages = RequireInRange(
            maximumConversationMessages,
            2,
            4 * 1024,
            nameof(maximumConversationMessages));
        MaximumConversationBytes = RequireInRange(
            maximumConversationBytes,
            MaximumAssistantTextBytes,
            16 * 1024 * 1024,
            nameof(maximumConversationBytes));
        MaximumRetainedEvents = RequireInRange(
            maximumRetainedEvents,
            1,
            16 * 1024,
            nameof(maximumRetainedEvents));
        MaximumEventBatchSize = RequireInRange(
            maximumEventBatchSize,
            1,
            Math.Min(MaximumRetainedEvents, 1024),
            nameof(maximumEventBatchSize));
        MaximumToolResultBytes = RequireInRange(
            maximumToolResultBytes,
            1,
            AgentToolResultValue.MaximumContentBytes,
            nameof(maximumToolResultBytes));
        MaximumToolResultsPerTurn = MaximumToolCallsPerTurn;
        MaximumTotalToolResultBytesPerTurn = RequireInRange(
            maximumTotalToolResultBytesPerTurn,
            MaximumToolResultBytes,
            Math.Min(
                16 * 1024 * 1024,
                AgentToolResultValue.MaximumContentBytes * MaximumToolCallsPerTurn),
            nameof(maximumTotalToolResultBytesPerTurn));
    }

    public static AgentKernelLimits Default { get; } = new();

    public int MaximumProviderTextFragmentBytes { get; }

    public int MaximumAssistantTextBytes { get; }

    public int MaximumProviderEventsPerTurn { get; }

    public int MaximumConcurrentProviderOperations { get; }

    public int MaximumToolDefinitions { get; }

    public int MaximumToolSchemaBytes { get; }

    public int MaximumTotalToolSchemaBytes { get; }

    public int MaximumToolCallsPerTurn { get; }

    public int MaximumToolArgumentFragmentBytes { get; }

    public int MaximumToolArgumentBytes { get; }

    public int MaximumTotalToolArgumentBytesPerTurn { get; }

    public int MaximumJsonDepth { get; }

    public int MaximumJsonNodes { get; }

    public int MaximumConversationMessages { get; }

    public int MaximumConversationBytes { get; }

    public int MaximumRetainedEvents { get; }

    public int MaximumEventBatchSize { get; }

    public int MaximumToolResultBytes { get; }

    public int MaximumToolResultsPerTurn { get; }

    public int MaximumTotalToolResultBytesPerTurn { get; }

    private static int RequireInRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The value must be between {minimum} and {maximum}.");
        }

        return value;
    }
}
