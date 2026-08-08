namespace GhostShell.Application;

/// <summary>A database object name with every qualification component intact.</summary>
public sealed record DatabaseObjectId(string? Catalog, string? Schema, string Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Schema)
        ? Name
        : $"{Schema}.{Name}";
}

public sealed record DatabaseColumnSchema(
    string Name,
    int Ordinal,
    string DataTypeName,
    DatabaseValueKind ValueKind,
    string? ClrTypeName = null,
    bool? IsNullable = null,
    bool IsPrimaryKey = false,
    int? PrimaryKeyOrdinal = null,
    bool IsIdentity = false,
    bool IsGenerated = false,
    bool IsReadOnly = false,
    string? DefaultExpression = null,
    long? Length = null,
    int? Precision = null,
    int? Scale = null)
{
    public bool CanEdit => !IsIdentity && !IsGenerated && !IsReadOnly;
}

public sealed record DatabaseIndexColumn(
    string? Name,
    int Ordinal,
    bool IsDescending = false,
    bool IsIncluded = false,
    string? Expression = null);

public sealed record DatabaseIndexSchema(
    string Name,
    string Kind,
    bool IsUnique,
    bool IsPrimary,
    bool IsValid,
    IReadOnlyList<DatabaseIndexColumn> Columns,
    string? Predicate = null,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record DatabaseObjectDetails(
    DatabaseTableDescriptor Object,
    IReadOnlyList<DatabaseColumnSchema> Columns,
    IReadOnlyList<DatabaseIndexSchema> Indexes,
    bool CanEdit,
    string? ReadOnlyReason = null)
{
    public IReadOnlyList<DatabaseColumnSchema> PrimaryKey => Columns
        .Where(column => column.IsPrimaryKey)
        .OrderBy(column => column.PrimaryKeyOrdinal ?? int.MaxValue)
        .ToArray();
}
