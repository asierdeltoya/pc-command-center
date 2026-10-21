using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.RegularExpressions;
using PCCommandCenter.Core;

namespace PCCommandCenter.Diagnostics.Logs;

/// <summary>
/// Parses text, CSV, JSON-lines and Windows .evtx logs; classifies severity, groups similar messages,
/// builds a timeline and detects patterns (recurring errors, bursts, escalation, periodicity, gaps and
/// well-known failure families).
/// </summary>
public sealed partial class LogAnalyzer : ILogAnalyzer
{
    private const int MaxLines = 3_000_000;
    private const int MaxStoredEntries = 400_000;

    public IReadOnlyList<string> SupportedExtensions { get; } = [".log", ".txt", ".csv", ".tsv", ".json", ".jsonl", ".evtx", ".out", ".err", ".trace", ".etl.txt", ".dmp.txt"];

    public async Task<LogAnalysisResult> AnalyzeFileAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(Loc.T("The file or folder no longer exists."), path);
        if (info.Extension.Equals(".evtx", StringComparison.OrdinalIgnoreCase))
            return await Task.Run(() => AnalyzeEvtx(path, info.Length, progress, ct), ct).ConfigureAwait(false);

        return await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Analyze(ReadLines(reader, stream, progress, ct), info.Name, info.Length, info.Extension, ct);
        }, ct).ConfigureAwait(false);
    }

    public Task<LogAnalysisResult> AnalyzeTextAsync(string text, string sourceName, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var reader = new StringReader(text);
            return Analyze(ReadLines(reader, null, null, ct), sourceName, Encoding.UTF8.GetByteCount(text), Path.GetExtension(sourceName), ct);
        }, ct);

    private static IEnumerable<string> ReadLines(TextReader reader, Stream? stream, IProgress<double>? progress, CancellationToken ct)
    {
        string? line;
        int n = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if ((++n & 0x3FFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (stream != null && stream.Length > 0) progress?.Report((double)stream.Position / stream.Length);
            }
            yield return line;
        }
    }

    // ───────────────────────────── Core analysis ─────────────────────────────

    private sealed class CsvLayout
    {
        public char Delimiter;
        public int Level = -1, Time = -1, Message = -1, Source = -1;
    }

    private LogAnalysisResult Analyze(IEnumerable<string> lines, string source, long size, string extension, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var entries = new List<LogEntry>(4096);
        var counts = new Dictionary<LogSeverity, int>();
        var groups = new Dictionary<string, LogGroup>();
        int lineNo = 0, timestamped = 0;
        bool truncated = false;
        CsvLayout? csv = null;
        string format = "Plain text";
        bool? hasTimestamps = null;
        var probe = new List<bool>(200);
        LogEntry? last = null;

        foreach (var raw in lines)
        {
            lineNo++;
            if (lineNo > MaxLines) { truncated = true; break; }
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Length > 4000 ? raw[..4000] : raw;

            if (lineNo == 1)
            {
                csv = DetectCsv(line, extension);
                if (csv != null) { format = csv.Delimiter == '\t' ? "TSV" : "CSV"; continue; }
                if (line.TrimStart().StartsWith('{')) format = "JSON lines";
            }

            DateTime? time;
            LogSeverity severity;
            string message;

            if (csv != null)
            {
                var cols = SplitCsv(line, csv.Delimiter);
                string Col(int i) => i >= 0 && i < cols.Count ? cols[i] : "";
                time = csv.Time >= 0 && LogLineParser.TryParseLooseTimestamp(Col(csv.Time), out var t) ? t : null;
                var levelText = Col(csv.Level);
                message = csv.Message >= 0 ? Col(csv.Message) : string.Join(" ", cols.Where((_, i) => i != csv.Time && i != csv.Level));
                if (csv.Source >= 0 && Col(csv.Source) is { Length: > 0 } src) message = $"[{src}] {message}";
                severity = levelText.Length > 0 ? MapCsvLevel(levelText) : LogLineParser.InferLevel(message);
            }
            else if (format == "JSON lines" && line.TrimStart().StartsWith('{'))
            {
                (time, severity, message) = ParseJsonLine(line);
            }
            else
            {
                var (ts, end) = LogLineParser.ParseTimestamp(line);
                time = ts;

                if (hasTimestamps == null)
                {
                    probe.Add(ts.HasValue);
                    if (probe.Count >= 200) hasTimestamps = probe.Count(x => x) > probe.Count / 2;
                }

                // Continuation lines (stack traces, wrapped messages) belong to the previous entry.
                bool continuation = last != null && !ts.HasValue &&
                                    (char.IsWhiteSpace(raw[0]) || (hasTimestamps ?? probe.Count(x => x) > probe.Count / 2) && LogLineParser.ParseExplicitLevel(line, 0) == null);
                if (continuation)
                {
                    if (last!.Message.Length < 600) last.Message += " ⏎ " + line.Trim();
                    continue;
                }

                severity = LogLineParser.ParseExplicitLevel(line, end) ?? LogLineParser.InferLevel(line);
                message = end > 0 && end < line.Length ? line[end..].TrimStart(' ', ']', '|', '-', ',', ':') : line;
                message = LogLineParser.StripLeadingLevel(message);
            }

            if (time.HasValue) timestamped++;
            counts[severity] = counts.GetValueOrDefault(severity) + 1;

            var entry = new LogEntry { LineNumber = lineNo, Timestamp = time, Severity = severity, Message = message.Trim() };
            if (entries.Count < MaxStoredEntries || severity >= LogSeverity.Warning) entries.Add(entry);
            else truncated = true;
            last = entry;
        }

        // Group warnings and above by normalized signature.
        foreach (var e in entries)
        {
            if (e.Severity < LogSeverity.Warning) continue;
            ct.ThrowIfCancellationRequested();
            e.Signature = LogLineParser.Signature(e.Message);
            if (!groups.TryGetValue(e.Signature, out var g))
            {
                g = new LogGroup { Signature = e.Signature, Severity = e.Severity, Sample = e.Message, FirstLine = e.LineNumber, First = e.Timestamp };
                groups[e.Signature] = g;
            }
            g.Count++;
            if (e.Severity > g.Severity) { g.Severity = e.Severity; g.Sample = e.Message; }
            if (e.Timestamp is { } t)
            {
                if (g.First == null || t < g.First) g.First = t;
                if (g.Last == null || t > g.Last) g.Last = t;
            }
        }

        var timed = entries.Where(e => e.Timestamp.HasValue).ToList();
        DateTime? from = timed.Count > 0 ? timed.Min(e => e.Timestamp) : null;
        DateTime? to = timed.Count > 0 ? timed.Max(e => e.Timestamp) : null;
        var (timeline, bucket) = BuildTimeline(timed, from, to);
        var orderedGroups = groups.Values.OrderByDescending(g => g.Severity).ThenByDescending(g => g.Count).ToList();
        var patterns = DetectPatterns(entries, timed, orderedGroups, counts);

        var result = new LogAnalysisResult
        {
            Source = source,
            Format = format,
            FileSize = size,
            TotalLines = lineNo,
            Truncated = truncated,
            Entries = entries,
            Counts = counts,
            Groups = orderedGroups,
            Patterns = patterns,
            Timeline = timeline,
            TimelineBucketSize = bucket,
            From = from,
            To = to,
            TimestampedEntries = timestamped,
            Elapsed = sw.Elapsed,
        };
        return WithSummary(result);
    }

    private static LogSeverity MapCsvLevel(string text)
    {
        var t = text.Trim().Trim('"');
        if (t.Length == 0) return LogSeverity.Info;
        if (t.StartsWith("crit", StringComparison.OrdinalIgnoreCase) || t.StartsWith("crít", StringComparison.OrdinalIgnoreCase)) return LogSeverity.Critical;
        if (t.StartsWith("err", StringComparison.OrdinalIgnoreCase)) return LogSeverity.Error;
        if (t.StartsWith("warn", StringComparison.OrdinalIgnoreCase) || t.StartsWith("adv", StringComparison.OrdinalIgnoreCase)) return LogSeverity.Warning;
        if (t.StartsWith("debug", StringComparison.OrdinalIgnoreCase) || t.StartsWith("verbose", StringComparison.OrdinalIgnoreCase) || t.StartsWith("detall", StringComparison.OrdinalIgnoreCase)) return LogSeverity.Debug;
        return LogLineParser.MapLevel(t);
    }

    private static CsvLayout? DetectCsv(string header, string extension)
    {
        bool hinted = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase);
        foreach (var d in new[] { ',', ';', '\t' })
        {
            var cols = SplitCsv(header, d);
            if (cols.Count < 2) continue;
            var layout = new CsvLayout { Delimiter = d };
            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i].Trim().Trim('"').ToLowerInvariant();
                if (layout.Level < 0 && c is "level" or "severity" or "type" or "loglevel" or "log level" or "nivel" or "entrytype" or "leveldisplayname") layout.Level = i;
                else if (layout.Time < 0 && (c.Contains("time") || c.Contains("date") || c is "fecha" or "fecha y hora" or "@t" or "ts")) layout.Time = i;
                else if (layout.Message < 0 && c is "message" or "msg" or "text" or "description" or "mensaje" or "details") layout.Message = i;
                else if (layout.Source < 0 && c is "source" or "provider" or "logger" or "origen" or "providername" or "component") layout.Source = i;
            }
            if (layout.Level >= 0 || layout.Time >= 0 || (hinted && layout.Message >= 0)) return layout;
        }
        return null;
    }

    private static List<string> SplitCsv(string line, char delimiter)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == delimiter && !quoted) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    [GeneratedRegex(@"""(?:level|severity|lvl|@l|loglevel)""\s*:\s*""?(?<v>[A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JsonLevel();

    [GeneratedRegex(@"""(?:timestamp|time|@t|ts|date|datetime)""\s*:\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonTime();

    [GeneratedRegex(@"""(?:message|msg|@m|@mt|text)""\s*:\s*""(?<v>(?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonMessage();

    private static (DateTime?, LogSeverity, string) ParseJsonLine(string line)
    {
        DateTime? time = null;
        var tm = JsonTime().Match(line);
        if (tm.Success && LogLineParser.TryParseLooseTimestamp(tm.Groups["v"].Value, out var t)) time = t;
        var mm = JsonMessage().Match(line);
        var message = mm.Success ? Regex.Unescape(mm.Groups["v"].Value) : line;
        var lm = JsonLevel().Match(line);
        var severity = lm.Success ? LogLineParser.MapLevel(lm.Groups["v"].Value) : LogLineParser.InferLevel(line);
        return (time, severity, message);
    }

    // ───────────────────────────── EVTX ─────────────────────────────

    private LogAnalysisResult AnalyzeEvtx(string path, long size, IProgress<double>? progress, CancellationToken ct)
    {
        var lines = new List<string>();
        var entries = new List<(DateTime? Time, LogSeverity Sev, string Msg)>();
        using (var reader = new EventLogReader(path, PathType.FilePath))
        {
            for (int i = 0; i < 200_000; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var r = reader.ReadEvent();
                if (r == null) break;
                string msg;
                try { msg = r.FormatDescription() ?? ""; } catch { msg = ""; }
                if (string.IsNullOrWhiteSpace(msg)) msg = $"Event {r.Id}";
                var sev = r.Level switch { 1 => LogSeverity.Critical, 2 => LogSeverity.Error, 3 => LogSeverity.Warning, 5 => LogSeverity.Debug, _ => LogSeverity.Info };
                entries.Add((r.TimeCreated, sev, $"[{r.ProviderName} {r.Id}] {msg.Replace("\r", " ").Replace('\n', ' ')}"));
                if ((i & 0x3FF) == 0) progress?.Report(Math.Min(0.95, i / 50_000.0));
            }
        }
        // Re-use the common pipeline through a synthetic CSV stream with an explicit header.
        IEnumerable<string> Synthetic()
        {
            yield return "Time\tLevel\tMessage";
            foreach (var (t, s, m) in entries.OrderBy(e => e.Time))
                yield return $"{t:yyyy-MM-dd HH:mm:ss}\t{s}\t{m.Replace('\t', ' ')}";
        }
        var result = Analyze(Synthetic(), Path.GetFileName(path), size, ".tsv", ct);
        return WithSummary(new LogAnalysisResult
        {
            Source = result.Source, Format = "Windows Event Log (.evtx)", FileSize = size, TotalLines = entries.Count, Truncated = result.Truncated,
            Entries = result.Entries, Counts = result.Counts, Groups = result.Groups, Patterns = result.Patterns, Timeline = result.Timeline,
            TimelineBucketSize = result.TimelineBucketSize, From = result.From, To = result.To, TimestampedEntries = result.TimestampedEntries,
            Elapsed = result.Elapsed,
        });
    }

    // ───────────────────────────── Timeline ─────────────────────────────

    private static readonly TimeSpan[] BucketSizes =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3),
        TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30),
    ];

    private static (IReadOnlyList<TimelineBucket>, TimeSpan) BuildTimeline(List<LogEntry> timed, DateTime? from, DateTime? to)
    {
        if (from == null || to == null || timed.Count < 2) return ([], TimeSpan.Zero);
        var span = to.Value - from.Value;
        var bucket = BucketSizes.FirstOrDefault(b => span.Ticks / b.Ticks <= 60, BucketSizes[^1]);
        var start = new DateTime(from.Value.Ticks - from.Value.Ticks % bucket.Ticks);
        int n = (int)((to.Value - start).Ticks / bucket.Ticks) + 1;
        var buckets = Enumerable.Range(0, n).Select(i => new TimelineBucket { Start = start + TimeSpan.FromTicks(bucket.Ticks * i) }).ToArray();
        foreach (var e in timed)
        {
            int i = (int)((e.Timestamp!.Value - start).Ticks / bucket.Ticks);
            if (i < 0 || i >= n) continue;
            switch (e.Severity)
            {
                case LogSeverity.Critical: buckets[i].Critical++; break;
                case LogSeverity.Error: buckets[i].Error++; break;
                case LogSeverity.Warning: buckets[i].Warning++; break;
                default: buckets[i].Info++; break;
            }
        }
        return (buckets, bucket);
    }

    // ───────────────────────────── Patterns ─────────────────────────────

    private static readonly (string Name, Regex Pattern, string Explanation)[] Families =
    [
        ("Timeouts", new(@"timed?\s?out|timeout|deadline exceeded|ETIMEDOUT", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Operations did not complete in time. Usually caused by an overloaded or unreachable remote service, slow network, or a blocked resource."),
        ("Connection failures", new(@"connection (refused|reset|closed|aborted|failed)|ECONNREFUSED|ECONNRESET|no route to host|host unreachable|network is unreachable|socket", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Network connections were refused or dropped. The target service may be down, a firewall may be blocking it, or the network is unstable."),
        ("Name resolution", new(@"could not resolve|name resolution|NXDOMAIN|getaddrinfo|unknown host|no such host|DNS", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Host names could not be resolved. Check the DNS configuration and that the host name is correct."),
        ("Memory pressure", new(@"out of memory|OutOfMemory|\bOOM\b|cannot allocate|insufficient memory|not enough memory|memory leak|heap", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "The application ran out of memory or failed allocations. Look for leaks or increase available memory."),
        ("Permission problems", new(@"access (is )?denied|permission denied|unauthori[sz]ed|forbidden|EACCES|EPERM|\b40[13]\b|privilege", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Operations were rejected for lack of rights. Check account permissions, file ACLs, credentials or tokens."),
        ("Disk and I/O", new(@"no space left|disk (is )?full|ENOSPC|insufficient disk|quota exceeded|I/O error|bad (sector|block)|read error|write error", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Storage errors: full disk or failing I/O. Free space and check the drive health."),
        ("Null references", new(@"NullReferenceException|NullPointerException|null reference|object reference not set|undefined is not|NoneType|nil pointer", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Code used a missing value. This is usually a software bug or an unexpected input; update the application or report it."),
        ("TLS / certificates", new(@"certificate|\bSSL\b|\bTLS\b|handshake|x509|trust relationship|CERT_", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Secure connections failed. Typical causes: expired/untrusted certificates, wrong system clock, or interception by a proxy/antivirus."),
        ("Crashes", new(@"segmentation fault|segfault|access violation|0xc0000005|core dumped|\bcrash|unhandled exception|fatal error|bugcheck|stack overflow", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "The process terminated abnormally. Check for updates, faulty plugins/drivers, or hardware instability (RAM, overclock)."),
        ("Database", new(@"deadlock|lock wait timeout|SQLSTATE|sql ?exception|database|constraint violation|duplicate key", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Database errors such as deadlocks or constraint violations. Review concurrent access and query logic."),
        ("Missing files", new(@"file not found|FileNotFoundException|ENOENT|no such file|cannot find (the )?(file|path)|could not find file|not found", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Expected files or resources were missing. A broken install, wrong path or deleted file is the usual cause."),
        ("Windows servicing", new(@"0x800[0-9a-fA-F]{5}|CBS_E_|component store|corrupt|DISM|Windows Update", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Windows component store or update errors. Running 'DISM /Online /Cleanup-Image /RestoreHealth' and 'sfc /scannow' usually repairs them."),
    ];

    [GeneratedRegex(@"\b([A-Z][\w.]*?(?:Exception|Error))\b")]
    private static partial Regex ExceptionTypeRegex();

    private static List<LogPattern> DetectPatterns(List<LogEntry> entries, List<LogEntry> timed, List<LogGroup> groups, Dictionary<LogSeverity, int> counts)
    {
        var patterns = new List<LogPattern>();
        var problems = entries.Where(e => e.Severity >= LogSeverity.Warning).ToList();
        int errorTotal = counts.GetValueOrDefault(LogSeverity.Error) + counts.GetValueOrDefault(LogSeverity.Critical);
        if (problems.Count == 0) return patterns;

        // 1. Recurring messages.
        foreach (var g in groups.Where(g => g.Count >= 5 && g.Count >= problems.Count * 0.03).Take(4))
        {
            var range = g.First.HasValue && g.Last.HasValue ? Loc.F(" between {0:g} and {1:g}", g.First, g.Last) : "";
            patterns.Add(new LogPattern
            {
                Title = Loc.F("Recurring {0}: repeated {1} times", Loc.T(g.Severity.ToString().ToLowerInvariant()), g.Count),
                Explanation = Loc.F("The same message (with varying numbers/IDs) appears {0} times{1}. A repeating failure usually has a single root cause worth fixing first.", g.Count, range),
                Severity = g.Severity, Occurrences = g.Count, Evidence = Truncate(g.Sample, 240),
            });
        }

        // 2. Bursts of errors in a 60 s window.
        var errorTimes = timed.Where(e => e.Severity >= LogSeverity.Error).Select(e => e.Timestamp!.Value).OrderBy(t => t).ToList();
        if (errorTimes.Count >= 10)
        {
            int best = 0, bestStart = 0, j = 0;
            for (int i = 0; i < errorTimes.Count; i++)
            {
                while (errorTimes[i] - errorTimes[j] > TimeSpan.FromSeconds(60)) j++;
                if (i - j + 1 > best) { best = i - j + 1; bestStart = j; }
            }
            var minutes = Math.Max(1, (errorTimes[^1] - errorTimes[0]).TotalMinutes);
            var rate = errorTimes.Count / minutes;
            if (best >= 10 && best >= rate * 5)
                patterns.Add(new LogPattern
                {
                    Title = Loc.F("Error burst at {0:g}", errorTimes[bestStart]),
                    Explanation = Loc.F("{0} errors occurred within one minute, far above the average rate of {1:0.##} per minute. Something failed suddenly at that moment (crash, outage, restart or dependency failure).", best, rate),
                    Severity = LogSeverity.Error, Occurrences = best,
                });
        }

        // 3. Escalation over time.
        if (timed.Count > 30 && errorTimes.Count >= 10)
        {
            var from = timed.Min(e => e.Timestamp!.Value);
            var to = timed.Max(e => e.Timestamp!.Value);
            var third = (to - from) / 3;
            if (third > TimeSpan.Zero)
            {
                int first = errorTimes.Count(t => t < from + third);
                int lastThird = errorTimes.Count(t => t >= to - third);
                if (lastThird >= 10 && lastThird >= Math.Max(1, first) * 2)
                    patterns.Add(new LogPattern
                    {
                        Title = Loc.T("Errors are increasing over time"),
                        Explanation = Loc.F("The last third of the log contains {0} errors versus {1} in the first third. The situation is getting worse rather than being a one-off event.", lastThird, first),
                        Severity = LogSeverity.Warning, Occurrences = lastThird,
                    });
            }
        }

        // 4. Known failure families.
        foreach (var (name, regex, explanation) in Families)
        {
            var hits = problems.Where(e => regex.IsMatch(e.Message)).ToList();
            if (hits.Count < 3) continue;
            patterns.Add(new LogPattern
            {
                Title = Loc.F("{0} ({1} occurrences)", Loc.T(name), hits.Count),
                Explanation = Loc.T(explanation),
                Severity = hits.Max(h => h.Severity), Occurrences = hits.Count, Evidence = Truncate(hits[0].Message, 240),
            });
        }

        // 5. Exception types.
        var exceptionTypes = problems.SelectMany(e => ExceptionTypeRegex().Matches(e.Message).Select(m => m.Groups[1].Value))
            .Where(t => t.Length > 6).GroupBy(t => t).Select(g => (Type: g.Key, Count: g.Count())).Where(x => x.Count >= 2)
            .OrderByDescending(x => x.Count).Take(5).ToList();
        if (exceptionTypes.Count > 0)
            patterns.Add(new LogPattern
            {
                Title = Loc.F("{0} exception type(s) detected", exceptionTypes.Count),
                Explanation = Loc.F("Most frequent: {0}.", string.Join(", ", exceptionTypes.Select(x => $"{x.Type} ×{x.Count}"))),
                Severity = LogSeverity.Error, Occurrences = exceptionTypes.Sum(x => x.Count),
            });

        // 6. Periodic errors.
        foreach (var g in groups.Where(g => g.Count >= 6).Take(10))
        {
            var times = timed.Where(e => e.Signature == g.Signature).Select(e => e.Timestamp!.Value).OrderBy(t => t).ToList();
            if (times.Count < 6) continue;
            var deltas = times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).Where(d => d > 0).ToList();
            if (deltas.Count < 5) continue;
            var mean = deltas.Average();
            var std = Math.Sqrt(deltas.Average(d => (d - mean) * (d - mean)));
            if (mean >= 5 && std / mean < 0.15)
            {
                patterns.Add(new LogPattern
                {
                    Title = Loc.F("Periodic {0} every ~{1}", Loc.T(g.Severity.ToString().ToLowerInvariant()), Fmt.Duration(TimeSpan.FromSeconds(mean))),
                    Explanation = Loc.T("This message repeats at a regular interval, which points to a scheduled task, timer, retry loop or polling component."),
                    Severity = g.Severity, Occurrences = times.Count, Evidence = Truncate(g.Sample, 240),
                });
                break;
            }
        }

        // 7. Gaps (silence) in logging.
        if (timed.Count > 50)
        {
            var ordered = timed.Select(e => e.Timestamp!.Value).OrderBy(t => t).ToList();
            var gaps = ordered.Zip(ordered.Skip(1), (a, b) => (Start: a, Length: b - a)).ToList();
            var median = gaps.Select(g => g.Length.Ticks).OrderBy(x => x).ElementAt(gaps.Count / 2);
            foreach (var gap in gaps.Where(g => g.Length > TimeSpan.FromMinutes(10) && g.Length.Ticks > median * 50).OrderByDescending(g => g.Length).Take(2))
                patterns.Add(new LogPattern
                {
                    Title = Loc.F("Logging gap of {0}", Fmt.Duration(gap.Length)),
                    Explanation = Loc.F("No entries between {0:g} and {1:g}. This can indicate the application or computer was stopped, hung or restarted.", gap.Start, gap.Start + gap.Length),
                    Severity = LogSeverity.Info, Occurrences = 1,
                });
        }

        return patterns
            .Select(p => p.Title.Length > 0 && char.IsLower(p.Title[0])
                ? new LogPattern { Title = char.ToUpper(p.Title[0]) + p.Title[1..], Explanation = p.Explanation, Severity = p.Severity, Occurrences = p.Occurrences, Evidence = p.Evidence }
                : p)
            .OrderByDescending(p => p.Severity).ThenByDescending(p => p.Occurrences).ToList();
    }

    private static LogAnalysisResult WithSummary(LogAnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Loc.F("Analyzed {0:N0} lines ({1}) from {2}.", r.TotalLines, r.Format, r.Source));
        if (r.From.HasValue && r.To.HasValue) sb.Append(' ').Append(Loc.F("Time range: {0:g} → {1:g} ({2}).", r.From, r.To, Fmt.Duration(r.To.Value - r.From.Value)));
        else sb.Append(' ').Append(Loc.T("No timestamps were recognized."));
        sb.Append(' ').Append(Loc.F("{0} critical, {1} errors, {2} warnings, {3} info.", r.Count(LogSeverity.Critical), r.Count(LogSeverity.Error), r.Count(LogSeverity.Warning), r.Count(LogSeverity.Info) + r.Count(LogSeverity.Debug)));
        var top = r.Groups.FirstOrDefault();
        if (top != null) sb.Append(' ').Append(Loc.F("Most frequent problem ({0}×): {1}", top.Count, Truncate(top.Sample, 140)));
        if (r.Truncated) sb.Append(' ').Append(Loc.T("The file is very large: only part of the informational lines were kept for display."));
        return new LogAnalysisResult
        {
            Source = r.Source, Format = r.Format, FileSize = r.FileSize, TotalLines = r.TotalLines, Truncated = r.Truncated, Entries = r.Entries,
            Counts = r.Counts, Groups = r.Groups, Patterns = r.Patterns, Timeline = r.Timeline, TimelineBucketSize = r.TimelineBucketSize,
            From = r.From, To = r.To, TimestampedEntries = r.TimestampedEntries, Elapsed = r.Elapsed, Summary = sb.ToString(),
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
