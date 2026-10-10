namespace ControlServer.Infrastructure.Persistence.Configurations;

/// <summary>Builds the SQL of a CHECK constraint that accepts a closed set of text values.</summary>
internal static class CheckConstraintSql
{
    /// <summary>
    /// <c>"Column" IN ('A', 'B')</c>, in the order given. The order is part of the migration's text, so it must depend on
    /// nothing but the constants.
    /// </summary>
    internal static string OneOf(string column, IEnumerable<string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(values);
        string[] quoted = [.. values.Select(Quote)];
        if (quoted.Length == 0)
        {
            throw new ArgumentException("A closed set needs at least one value.", nameof(values));
        }
        return $"\"{column}\" IN ({string.Join(", ", quoted)})";
    }

    private static string Quote(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
