using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Recon.Engine.Sql;

/// <summary>
/// Collects the parameters a compiled statement needs.
///
/// Every literal a user configured — a filter value, a tolerance, a date
/// window — arrives here and is bound as a parameter. Nothing in
/// <see cref="Recon.Engine.Sql"/> concatenates a user value into SQL text, and
/// that is the property the whole rule builder rests on.
/// </summary>
public sealed class SqlParameterBag
{
    private readonly List<SqlParameter> _parameters = [];
    private int _next;

    public IReadOnlyList<SqlParameter> Parameters => _parameters;

    /// <summary>Adds a value and returns the parameter name to put in the SQL.</summary>
    public string Add(object? value)
    {
        var name = "@p" + _next++;
        _parameters.Add(new SqlParameter(name, value ?? DBNull.Value));
        return name;
    }

    public string Add(string name, object? value)
    {
        if (!name.StartsWith('@'))
        {
            name = "@" + name;
        }

        _parameters.Add(new SqlParameter(name, value ?? DBNull.Value));
        return name;
    }

    /// <summary>
    /// Binds a JSON value from a condition tree, choosing the SQL type from the
    /// field's declared type rather than from the JSON. A value that cannot be
    /// read as the field's type is a configuration error, not something to
    /// coerce silently.
    /// </summary>
    public string AddJson(JsonElement value, Domain.Configuration.FieldDataType type)
    {
        object bound = type switch
        {
            Domain.Configuration.FieldDataType.Integer => ReadInteger(value),
            Domain.Configuration.FieldDataType.Decimal => ReadDecimal(value),
            Domain.Configuration.FieldDataType.DateTime => ReadDateTime(value),
            Domain.Configuration.FieldDataType.Boolean => ReadBoolean(value),
            _ => ReadString(value),
        };

        var name = "@p" + _next++;
        var p = new SqlParameter(name, bound)
        {
            SqlDbType = type switch
            {
                Domain.Configuration.FieldDataType.Integer => SqlDbType.BigInt,
                Domain.Configuration.FieldDataType.Decimal => SqlDbType.Decimal,
                Domain.Configuration.FieldDataType.DateTime => SqlDbType.DateTime2,
                Domain.Configuration.FieldDataType.Boolean => SqlDbType.Bit,
                _ => SqlDbType.NVarChar,
            },
        };

        if (p.SqlDbType == SqlDbType.Decimal)
        {
            p.Precision = 18;
            p.Scale = 3;
        }
        else if (p.SqlDbType == SqlDbType.NVarChar)
        {
            // Matches the slot width in the schema.
            p.Size = 300;
        }

        _parameters.Add(p);
        return name;
    }

    private static long ReadInteger(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
        _ => throw new SqlCompilationException($"value {v} is not an integer"),
    };

    private static decimal ReadDecimal(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
        JsonValueKind.String when decimal.TryParse(
            v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new SqlCompilationException($"value {v} is not a decimal"),
    };

    private static DateTime ReadDateTime(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String when DateTime.TryParse(
            v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) => d,
        _ => throw new SqlCompilationException($"value {v} is not a date"),
    };

    private static bool ReadBoolean(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when v.TryGetInt32(out var n) => n != 0,
        JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
        _ => throw new SqlCompilationException($"value {v} is not a boolean"),
    };

    private static string ReadString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
        _ => throw new SqlCompilationException($"value {v} is not a string"),
    };
}

public sealed class SqlCompilationException(string message) : Exception(message);
