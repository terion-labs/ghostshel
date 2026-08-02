namespace GhostShell.Core;

/// <summary>
/// The durable address of a database viewer panel: a driver id and its ADO.NET
/// connection string, serialized as "driverId:connection string" into the slots
/// that carry panel locations (screen startup location, recovery payloads).
/// The first colon splits the two, so driver ids never contain one.
/// </summary>
public sealed record DatabasePanelTarget(string DriverId, string ConnectionString)
{
    public string Serialize() => $"{DriverId}:{ConnectionString}";

    public static DatabasePanelTarget? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            return null;
        }

        var driverId = value[..separator];
        var connectionString = value[(separator + 1)..];
        return driverId.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new DatabasePanelTarget(driverId, connectionString);
    }
}
