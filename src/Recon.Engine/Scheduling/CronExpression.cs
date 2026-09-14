using System.Globalization;

namespace Recon.Engine.Scheduling;

/// <summary>
/// A five-field cron expression: minute, hour, day-of-month, month,
/// day-of-week.
///
/// <para>
/// Hand-written rather than taken from a package because what this needs is
/// narrow and the semantics must be exact: a schedule that fires twice, or
/// skips a session, is a reconciliation that did not run. Supports
/// <c>*</c>, a list (<c>1,15</c>), a range (<c>9-17</c>), a step
/// (<c>*/15</c>, <c>0-30/10</c>) and a named day or month.
/// </para>
///
/// <para>
/// Evaluated in the schedule's own time zone
/// (<c>cfg.ScheduleDefinition.TimeZone</c>), not the server's. Jordan is
/// UTC+3 with no DST, so a server in UTC firing "at 22:00" would fire at 01:00
/// local — the wrong business date.
/// </para>
/// </summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    public string Expression { get; }

    private CronExpression(string expression, string[] fields)
    {
        Expression = expression;

        Parse(fields[0], _minutes, 0, 59, nameof(_minutes));
        Parse(fields[1], _hours, 0, 23, nameof(_hours));
        Parse(fields[2], _daysOfMonth, 1, 31, nameof(_daysOfMonth));
        Parse(fields[3], _months, 1, 12, nameof(_months), MonthNames);
        Parse(fields[4], _daysOfWeek, 0, 6, nameof(_daysOfWeek), DayNames);

        _dayOfMonthRestricted = fields[2] != "*";
        _dayOfWeekRestricted = fields[4] != "*";
    }

    public static CronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length != 5)
        {
            throw new FormatException(
                $"'{expression}' has {fields.Length} fields; a cron expression has 5 " +
                "(minute hour day-of-month month day-of-week).");
        }

        return new CronExpression(expression, fields);
    }

    public static bool TryParse(string? expression, out CronExpression? cron)
    {
        cron = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        try
        {
            cron = Parse(expression);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the expression matches this local minute.
    ///
    /// <para>
    /// When BOTH day-of-month and day-of-week are restricted, cron's
    /// traditional semantics are OR, not AND — "1 * * 1 5" means the 1st of
    /// the month and also every Friday. Getting this backwards silently drops
    /// firings, so it is explicit here.
    /// </para>
    /// </summary>
    public bool Matches(DateTime local)
    {
        if (!_minutes[local.Minute] || !_hours[local.Hour] || !_months[local.Month])
        {
            return false;
        }

        var dayOfMonth = _daysOfMonth[local.Day];
        var dayOfWeek = _daysOfWeek[(int)local.DayOfWeek];

        return (_dayOfMonthRestricted, _dayOfWeekRestricted) switch
        {
            (false, false) => true,
            (true, false) => dayOfMonth,
            (false, true) => dayOfWeek,
            (true, true) => dayOfMonth || dayOfWeek,
        };
    }

    /// <summary>
    /// The next firing at or after <paramref name="after"/>, searching minute
    /// by minute. Bounded to a year: an expression like "0 0 30 2 *" —
    /// 30 February — never matches, and a scheduler must not spin looking.
    /// </summary>
    public DateTime? Next(DateTime after)
    {
        var candidate = new DateTime(
            after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind)
            .AddMinutes(1);

        var limit = candidate.AddYears(1);

        while (candidate < limit)
        {
            if (Matches(candidate))
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);
        }

        return null;
    }

    private static readonly Dictionary<string, int> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUN"] = 0, ["MON"] = 1, ["TUE"] = 2, ["WED"] = 3,
        ["THU"] = 4, ["FRI"] = 5, ["SAT"] = 6,
    };

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAY"] = 5, ["JUN"] = 6,
        ["JUL"] = 7, ["AUG"] = 8, ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12,
    };

    private static void Parse(
        string field,
        bool[] slots,
        int min,
        int max,
        string name,
        Dictionary<string, int>? names = null)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = 1;
            var body = part;

            var slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                body = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], CultureInfo.InvariantCulture, out step)
                    || step < 1)
                {
                    throw new FormatException($"'{part}' has an invalid step in {name}.");
                }
            }

            int from, to;

            if (body == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = body.IndexOf('-', StringComparison.Ordinal);

                if (dash > 0)
                {
                    from = Value(body[..dash], min, max, name, names);
                    to = Value(body[(dash + 1)..], min, max, name, names);
                }
                else
                {
                    from = Value(body, min, max, name, names);
                    // A bare value with a step means "from here onwards":
                    // 5/10 in the minute field is 5, 15, 25, and so on.
                    to = slash >= 0 ? max : from;
                }
            }

            if (from > to)
            {
                throw new FormatException($"'{part}' is a reversed range in {name}.");
            }

            for (var value = from; value <= to; value += step)
            {
                slots[value] = true;
            }
        }
    }

    private static int Value(
        string token, int min, int max, string field, Dictionary<string, int>? names)
    {
        int value;

        if (names is not null && names.TryGetValue(token, out var named))
        {
            value = named;
        }
        else if (!int.TryParse(token, CultureInfo.InvariantCulture, out value))
        {
            throw new FormatException($"'{token}' is not a value {field} accepts.");
        }

        // Cron allows 7 for Sunday as well as 0.
        if (max == 6 && value == 7)
        {
            value = 0;
        }

        if (value < min || value > max)
        {
            throw new FormatException($"{value} is outside {min}..{max} in {field}.");
        }

        return value;
    }

    public override string ToString() => Expression;
}
