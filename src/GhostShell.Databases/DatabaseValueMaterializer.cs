using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GhostShell.Application;

namespace GhostShell.Databases;

/// <summary>
/// Detaches values from an active provider reader and creates bounded display
/// text. Only known immutable values and explicit copies cross this boundary;
/// provider-specific objects degrade to display-only text.
/// </summary>
internal static class DatabaseValueMaterializer
{
    public const int DefaultMaxDisplayCharacters = 4_096;

    private const int BinaryPreviewByteCount = 32;
    private const int CollectionPreviewValueCount = 32;

    public static IReadOnlyList<DatabaseColumnDescriptor> DescribeColumns(DbDataReader reader) =>
        DatabaseColumnMaterializer.DescribeColumns(reader);

    public static DatabaseValue Materialize(
        DbDataReader reader,
        int ordinal,
        DatabaseColumnDescriptor column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(column);

        return reader.IsDBNull(ordinal)
            ? new DatabaseValue(null, column.ValueKind, "NULL")
            : FromProviderValue(
                reader.GetValue(ordinal),
                column.ValueKind,
                column.DataTypeName);
    }

    public static IReadOnlyList<DatabaseColumnDescriptor> ReconcileColumnSafety(
        IReadOnlyList<DatabaseColumnDescriptor> columns,
        IReadOnlyList<IReadOnlyList<DatabaseValue>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var unsafeOrdinals = new bool[columns.Count];
        foreach (var row in rows)
        {
            for (var ordinal = 0; ordinal < Math.Min(row.Count, columns.Count); ordinal++)
            {
                var value = row[ordinal];
                if (!value.IsNull && value.Kind == DatabaseValueKind.Other)
                {
                    unsafeOrdinals[ordinal] = true;
                }
            }
        }

        return [.. columns
            .Select((column, ordinal) => unsafeOrdinals[ordinal]
                ? column with
                {
                    ValueKind = DatabaseValueKind.Other,
                    IsReadOnly = true,
                }
                : column)];
    }

    public static DatabaseValue FromProviderValue(
        object? value,
        DatabaseValueKind declaredKind = DatabaseValueKind.Other,
        string? providerTypeName = null,
        int maxDisplayCharacters = DefaultMaxDisplayCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDisplayCharacters, 1);

        if (value is null or DBNull)
        {
            return new DatabaseValue(null, declaredKind, "NULL");
        }

        var kind = declaredKind == DatabaseValueKind.Other
            ? DatabaseValueClassifier.Classify(value.GetType(), providerTypeName, value)
            : declaredKind;
        if (!TryDetach(value, out var detached)
            && !TryNormalizeConvertibleDecimal(value, declaredKind, out detached))
        {
            // `Other` is the editability signal: callers may show this text but
            // must never send a provider-owned object back after its reader closes.
            var displayOnly = Bound(FormatInvariant(value), maxDisplayCharacters);
            return new DatabaseValue(
                displayOnly.Text,
                DatabaseValueKind.Other,
                displayOnly.Text,
                displayOnly.IsTruncated);
        }

        var display = FormatDetachedValue(detached, maxDisplayCharacters);
        return new DatabaseValue(detached, kind, display.Text, display.IsTruncated);
    }

    private static bool TryDetach(object value, out object detached)
    {
        switch (value)
        {
            case byte[] bytes:
                detached = bytes.ToArray();
                return true;
            case char[] characters:
                detached = new string(characters);
                return true;
            case Memory<byte> bytes:
                detached = bytes.ToArray();
                return true;
            case ReadOnlyMemory<byte> bytes:
                detached = bytes.ToArray();
                return true;
            case Memory<char> characters:
                detached = characters.ToString();
                return true;
            case ReadOnlyMemory<char> characters:
                detached = characters.ToString();
                return true;
            case Stream stream when TryCopyStream(stream, out var bytes):
                detached = bytes;
                return true;
            case JsonDocument document:
                detached = document.RootElement.Clone();
                return true;
            case JsonElement element:
                detached = element.Clone();
                return true;
            case IPAddress address:
                detached = CloneAddress(address);
                return true;
            case Array array when IsSafeArrayElement(array.GetType().GetElementType()):
                detached = array.Clone();
                return true;
        }

        if (IsSafeScalar(value.GetType()))
        {
            detached = value;
            return true;
        }

        detached = string.Empty;
        return false;
    }

    private static bool TryCopyStream(Stream stream, out byte[] bytes)
    {
        try
        {
            var originalPosition = stream.CanSeek ? stream.Position : 0;
            try
            {
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                using var copy = stream.CanSeek && stream.Length <= int.MaxValue
                    ? new MemoryStream((int)stream.Length)
                    : new MemoryStream();
                stream.CopyTo(copy);
                bytes = copy.ToArray();
                return true;
            }
            finally
            {
                if (stream.CanSeek)
                {
                    stream.Position = originalPosition;
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or NotSupportedException
            or ObjectDisposedException)
        {
            bytes = [];
            return false;
        }
    }

    private static bool TryNormalizeConvertibleDecimal(
        object value,
        DatabaseValueKind declaredKind,
        out object detached)
    {
        if (declaredKind != DatabaseValueKind.Decimal || value is not IConvertible)
        {
            detached = string.Empty;
            return false;
        }

        try
        {
            detached = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException
            or InvalidCastException
            or OverflowException)
        {
            detached = string.Empty;
            return false;
        }
    }

    private static bool IsSafeScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum || Type.GetTypeCode(type) != TypeCode.Object)
        {
            return true;
        }

        return type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || type == typeof(Half)
            || type == typeof(Int128)
            || type == typeof(UInt128)
            || type == typeof(BigInteger);
    }

    private static bool IsSafeArrayElement(Type? elementType) =>
        elementType is not null && IsSafeScalar(elementType);

    private static IPAddress CloneAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? new IPAddress(bytes, address.ScopeId)
            : new IPAddress(bytes);
    }

    private static DisplayValue FormatDetachedValue(
        object value,
        int maxDisplayCharacters)
    {
        DisplayValue formatted = value switch
        {
            byte[] bytes => FormatBinary(bytes),
            JsonElement json => new DisplayValue(json.GetRawText(), false),
            Array array => FormatCollection(array),
            _ => new DisplayValue(FormatInvariant(value), false),
        };
        var bounded = Bound(formatted.Text, maxDisplayCharacters);
        return bounded with
        {
            IsTruncated = formatted.IsTruncated || bounded.IsTruncated,
        };
    }

    private static DisplayValue FormatBinary(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, BinaryPreviewByteCount);
        var text = $"0x{Convert.ToHexString(bytes.AsSpan(0, length))}";
        if (bytes.Length <= BinaryPreviewByteCount)
        {
            return new DisplayValue(text, false);
        }

        return new DisplayValue($"{text}… ({bytes.Length} bytes)", true);
    }

    private static DisplayValue FormatCollection(Array values)
    {
        var preview = new List<string>(Math.Min(values.Length, CollectionPreviewValueCount));
        foreach (var value in values)
        {
            if (preview.Count == CollectionPreviewValueCount)
            {
                break;
            }

            preview.Add(value is null ? "NULL" : FormatInvariant(value));
        }

        var text = new StringBuilder("[")
            .AppendJoin(", ", preview)
            .Append(']')
            .ToString();
        return values.Length > CollectionPreviewValueCount
            ? new DisplayValue($"{text}… ({values.Length} values)", true)
            : new DisplayValue(text, false);
    }

    private static string FormatInvariant(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        JsonElement json => json.GetRawText(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? value.GetType().Name,
    };

    private static DisplayValue Bound(string text, int maxDisplayCharacters)
    {
        if (text.Length <= maxDisplayCharacters)
        {
            return new DisplayValue(text, false);
        }

        return maxDisplayCharacters == 1
            ? new DisplayValue("…", true)
            : new DisplayValue($"{text[..(maxDisplayCharacters - 1)]}…", true);
    }

    private readonly record struct DisplayValue(string Text, bool IsTruncated);
}
