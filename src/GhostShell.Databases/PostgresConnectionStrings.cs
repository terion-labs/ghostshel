using Npgsql;

namespace GhostShell.Databases;

/// <summary>
/// The URL every managed Postgres hands out, turned into what the driver reads.
///
/// Neon, Supabase, Railway, Fly, Heroku and psql itself all speak
/// <c>postgresql://user:password@host/database?sslmode=require</c>. Npgsql does
/// not: it takes keyword/value pairs, and given a URL it treats the whole thing
/// as one unknown keyword and says "Couldn't set postgresql://…" — which names
/// the string back at the person who pasted it and explains nothing.
///
/// So the URL is taken apart here, once, before any of it reaches the driver.
/// Anything that is already keyword/value passes through untouched.
/// </summary>
internal static class PostgresConnectionStrings
{
    private static readonly string[] Schemes = ["postgresql://", "postgres://"];

    public static string Normalize(string? connectionString)
    {
        var text = connectionString?.Trim() ?? string.Empty;
        var scheme = Schemes.FirstOrDefault(candidate =>
            text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        if (scheme is null)
        {
            return text;
        }

        var rest = text[scheme.Length..];
        var (rest1, query) = SplitAt(rest, '?');
        var (authority, database) = SplitAt(rest1, '/');
        // The last '@', not the first: a password may contain one, and the
        // host part may not.
        var separator = authority.LastIndexOf('@');
        var credentials = separator < 0 ? string.Empty : authority[..separator];
        var hosts = separator < 0 ? authority : authority[(separator + 1)..];

        var builder = new NpgsqlConnectionStringBuilder();
        ApplyHosts(builder, hosts);
        ApplyCredentials(builder, credentials);
        if (Decode(database) is { Length: > 0 } name)
        {
            builder.Database = name;
        }

        ApplyParameters(builder, query);
        return builder.ConnectionString;
    }

    private static void ApplyHosts(NpgsqlConnectionStringBuilder builder, string hosts)
    {
        if (hosts.Length == 0)
        {
            return;
        }

        // libpq allows a comma-separated list for failover. One host with a
        // port is the ordinary case; a list goes across whole, because the port
        // then belongs to each entry rather than to the connection.
        if (!hosts.Contains(',', StringComparison.Ordinal)
            && hosts.LastIndexOf(':') is var colon and >= 0
            && int.TryParse(hosts[(colon + 1)..], out var port))
        {
            builder.Host = Decode(hosts[..colon]);
            builder.Port = port;
            return;
        }

        builder.Host = Decode(hosts);
    }

    private static void ApplyCredentials(
        NpgsqlConnectionStringBuilder builder,
        string credentials)
    {
        if (credentials.Length == 0)
        {
            return;
        }

        // The first ':' — a password may contain one, a username may not.
        var separator = credentials.IndexOf(':');
        builder.Username = Decode(separator < 0 ? credentials : credentials[..separator]);
        if (separator >= 0)
        {
            builder.Password = Decode(credentials[(separator + 1)..]);
        }
    }

    /// <summary>
    /// libpq's spelling of a parameter, where Npgsql spells it differently.
    /// Npgsql answers to a few of these itself — <c>sslmode</c> among them —
    /// but not to most, and a URL from a hosting provider is written in libpq's
    /// vocabulary because that is the vocabulary psql reads.
    /// </summary>
    private static readonly Dictionary<string, string> LibpqNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["channel_binding"] = "Channel Binding",
            ["sslnegotiation"] = "SSL Negotiation",
            ["sslcert"] = "SSL Certificate",
            ["sslkey"] = "SSL Key",
            ["sslpassword"] = "SSL Password",
            ["sslrootcert"] = "Root Certificate",
            ["application_name"] = "Application Name",
            ["client_encoding"] = "Client Encoding",
            ["connect_timeout"] = "Timeout",
            ["target_session_attrs"] = "Target Session Attributes",
            ["passfile"] = "Passfile",
            ["options"] = "Options",
        };

    /// <summary>
    /// Each parameter under whichever name the driver knows it by. One it does
    /// not know at all is refused by name rather than dropped: silently
    /// discarding, say, an sslrootcert would weaken the connection without
    /// saying so.
    /// </summary>
    private static void ApplyParameters(NpgsqlConnectionStringBuilder builder, string query)
    {
        if (query.Length == 0)
        {
            return;
        }

        var unsupported = new List<string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = SplitAt(pair, '=');
            var name = Decode(key);
            if (name.Length == 0)
            {
                continue;
            }

            try
            {
                builder[LibpqNames.GetValueOrDefault(name, name)] = Decode(value);
            }
            catch (Exception exception) when (exception is ArgumentException
                or FormatException)
            {
                unsupported.Add(name);
            }
        }

        if (unsupported.Count > 0)
        {
            throw new ArgumentException(
                $"This build's PostgreSQL driver does not understand "
                + $"{string.Join(", ", unsupported)}. Remove "
                + (unsupported.Count == 1 ? "it" : "them")
                + " from the URL, or write the connection out as "
                + "Host=…;Port=…;Database=…;Username=…;Password=… instead.");
        }
    }

    private static (string Before, string After) SplitAt(string text, char separator)
    {
        var index = text.IndexOf(separator);
        return index < 0
            ? (text, string.Empty)
            : (text[..index], text[(index + 1)..]);
    }

    private static string Decode(string text) =>
        text.Length == 0 ? text : Uri.UnescapeDataString(text);
}
