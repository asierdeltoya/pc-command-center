using System.Globalization;
using System.Text.RegularExpressions;
using PCCommandCenter.Core;

namespace PCCommandCenter.Diagnostics.Logs;

/// <summary>Extracts timestamps and severities from individual log lines and builds grouping signatures.</summary>
internal static partial class LogLineParser
{
    private static readonly bool DayFirst =
        CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('d');

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    [GeneratedRegex(@"(\d{4})[-/](\d{2})[-/](\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,7}))?(Z|[+-]\d{2}:?\d{2})?")]
    private static partial Regex IsoRegex();

    [GeneratedRegex(@"\b(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})[ ,T]+(\d{1,2}):(\d{2}):(\d{2})(?:[.,](\d{1,7}))?(?:\s*([AaPp][Mm]))?")]
    private static partial Regex DmyRegex();

    [GeneratedRegex(@"\[(\d{2})/([A-Za-z]{3})/(\d{4}):(\d{2}):(\d{2}):(\d{2})")]
    private static partial Regex ApacheRegex();

    [GeneratedRegex(@"^([A-Z][a-z]{2})\s+(\d{1,2})\s(\d{2}):(\d{2}):(\d{2})")]
    private static partial Regex SyslogRegex();

    // Explicit level markers: [ERROR], <Warning>, level=error, "level":"warn", " ERROR ", "| WARN |", ", Info " (CBS)
    [GeneratedRegex(@"(?:\[|<|\(|\blevel[""']?\s*[:=]\s*[""']?|\bseverity[""']?\s*[:=]\s*[""']?|\|\s*|,\s+|^|\s)(?<lvl>fatal|critical|crit|emerg(?:ency)?|alert|panic|error|err|severe|fail|warn(?:ing)?|notice|info(?:rmation)?|debug|trace|verbose|dbg|inf|wrn|ftl|crt|eror)(?=\]|>|\)|[""']|\s*\||:|\s|,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LevelRegex();

    [GeneratedRegex(@"\b(exception|fatal|crash(?:ed)?|unhandled|traceback|failed|failure|error|errno|cannot|could not|unable to)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorWordsRegex();

    [GeneratedRegex(@"\b(warn(?:ing)?|deprecated|retry(?:ing)?|slow)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningWordsRegex();

    [GeneratedRegex(@"""\s(\d{3})\s")]
    private static partial Regex HttpStatusRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?\b")]
    private static partial Regex IpRegex();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b|\b[0-9a-fA-F]{12,}\b")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""'<>|]+|(?<![\w])/(?:[\w.\-]+/)+[\w.\-]*")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"""[^""]{1,200}""|'[^']{1,200}'")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    public static (DateTime? Time, int End) ParseTimestamp(string line)
    {
        var head = line.Length > 120 ? line[..120] : line;
        var m = IsoRegex().Match(head);
        if (m.Success && TryBuild(Int(m, 1), Int(m, 2), Int(m, 3), Int(m, 4), Int(m, 5), Int(m, 6), m.Groups[7].Value, out var iso))
        {
            if (m.Groups[8].Success)
            {
                // Has an explicit zone: convert to local time for a consistent timeline.
                if (DateTimeOffset.TryParse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)) iso = dto.LocalDateTime;
            }
            return (iso, m.Index + m.Length);
        }

        m = ApacheRegex().Match(head);
        if (m.Success)
        {
            int month = Array.IndexOf(Months, m.Groups[2].Value.ToLowerInvariant()) + 1;
            if (month > 0 && TryBuild(Int(m, 3), month, Int(m, 1), Int(m, 4), Int(m, 5), Int(m, 6), "", out var ap)) return (ap, m.Index + m.Length);
        }

        m = DmyRegex().Match(head);
        if (m.Success)
        {
            int a = Int(m, 1), b = Int(m, 2);
            bool dayFirst = a > 12 || (b <= 12 && DayFirst);
            int day = dayFirst ? a : b, month = dayFirst ? b : a;
            int hour = Int(m, 4);
            if (m.Groups[8].Success)
            {
                var pm = m.Groups[8].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
                if (pm && hour < 12) hour += 12;
                if (!pm && hour == 12) hour = 0;
            }
            if (TryBuild(Int(m, 3), month, day, hour, Int(m, 5), Int(m, 6), m.Groups[7].Value, out var dmy)) return (dmy, m.Index + m.Length);
        }

        m = SyslogRegex().Match(head);
        if (m.Success)
        {
            int month = Array.IndexOf(Months, m.Groups[1].Value.ToLowerInvariant()) + 1;
            if (month > 0 && TryBuild(DateTime.Now.Year, month, Int(m, 2), Int(m, 3), Int(m, 4), Int(m, 5), "", out var sys))
            {
                if (sys > DateTime.Now.AddDays(2)) sys = sys.AddYears(-1);
                return (sys, m.Index + m.Length);
            }
        }
        return (null, 0);
    }

    public static bool TryParseLooseTimestamp(string text, out DateTime time)
    {
        var (t, _) = ParseTimestamp(text);
        if (t.HasValue) { time = t.Value; return true; }
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out time)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time);
    }

    private static int Int(Match m, int g) => int.Parse(m.Groups[g].ValueSpan, CultureInfo.InvariantCulture);

    private static bool TryBuild(int y, int mo, int d, int h, int mi, int s, string frac, out DateTime result)
    {
        result = default;
        if (y < 1990 || y > 2100 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59) return false;
        result = new DateTime(y, mo, d, h, mi, s);
        if (frac.Length > 0) result = result.AddTicks(long.Parse(frac.PadRight(7, '0')[..7], CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Returns the explicit severity of a line, or null when the line carries no level marker.</summary>
    public static LogSeverity? ParseExplicitLevel(string line, int searchFrom)
    {
        var region = line.Length - searchFrom > 200 ? line.Substring(searchFrom, 200) : line[Math.Min(searchFrom, line.Length)..];
        var m = LevelRegex().Match(region);
        if (!m.Success) return null;
        var token = m.Groups["lvl"].Value;
        // Lower-case words like "error" in prose are only trusted when bracketed or key=value.
        int after = m.Index + m.Length;
        char next = after < region.Length ? region[after] : ' ';
        bool trusted = token.All(char.IsUpper)
                       || m.Value.IndexOfAny(['[', '<', '(', '=', '|', ',', ':']) >= 0
                       || next is ']' or '>' or ')' or ':' or '|';
        if (!trusted) return null;
        return MapLevel(token);
    }

    public static LogSeverity MapLevel(string token) => token.ToLowerInvariant() switch
    {
        "fatal" or "critical" or "crit" or "emerg" or "emergency" or "alert" or "panic" or "ftl" or "crt" => LogSeverity.Critical,
        "error" or "err" or "severe" or "fail" or "eror" => LogSeverity.Error,
        "warn" or "warning" or "wrn" => LogSeverity.Warning,
        "debug" or "trace" or "verbose" or "dbg" => LogSeverity.Debug,
        "1" => LogSeverity.Critical,
        "2" => LogSeverity.Error,
        "3" => LogSeverity.Warning,
        _ => LogSeverity.Info,
    };

    [GeneratedRegex(@"^\s*[\[<(]?(fatal|critical|crit|error|err|warn(?:ing)?|info(?:rmation)?|notice|debug|trace|verbose|dbg|inf|wrn|ftl)[\]>)]?\s*[:|-]?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingLevelRegex();

    /// <summary>Removes a leading level marker ("Warning  ", "[ERROR] ") so the message text starts with its content.</summary>
    public static string StripLeadingLevel(string message)
    {
        var m = LeadingLevelRegex().Match(message);
        return m.Success && m.Length < message.Length ? message[m.Length..] : message;
    }

    public static LogSeverity InferLevel(string line)
    {
        var http = HttpStatusRegex().Match(line);
        if (http.Success && int.TryParse(http.Groups[1].Value, out var code))
            return code >= 500 ? LogSeverity.Error : code >= 400 ? LogSeverity.Warning : LogSeverity.Info;
        if (ErrorWordsRegex().IsMatch(line)) return LogSeverity.Error;
        if (WarningWordsRegex().IsMatch(line)) return LogSeverity.Warning;
        return LogSeverity.Info;
    }

    public static string Signature(string message)
    {
        var s = message.Length > 400 ? message[..400] : message;
        s = GuidRegex().Replace(s, "<GUID>");
        s = IpRegex().Replace(s, "<IP>");
        s = PathRegex().Replace(s, "<PATH>");
        s = QuotedRegex().Replace(s, "<STR>");
        s = HexRegex().Replace(s, "<HEX>");
        s = NumberRegex().Replace(s, "<N>");
        s = SpaceRegex().Replace(s, " ").Trim();
        return s.Length > 180 ? s[..180] : s;
    }
}
