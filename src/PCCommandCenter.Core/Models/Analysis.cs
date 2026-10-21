namespace PCCommandCenter.Core;

// ───────────────────────────── Health ─────────────────────────────

public enum HealthStatus { Good, Warning, Critical, NotAvailable }

public enum IssueSeverity { Info, Warning, Critical }

/// <summary>Navigation targets a finding can point the user to.</summary>
public enum IssueAction { None, OpenProcesses, OpenStorage, OpenStartup, OpenNetwork, OpenWindows, OpenDashboard, OpenSecurity, OpenFixes, OpenApps }

public sealed class HealthIssue
{
    public string Category { get; init; } = "";
    public IssueSeverity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public IssueAction Action { get; init; }
}

public sealed class HealthCategory
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public HealthStatus Status { get; set; }
    public string Summary { get; set; } = "";
    public List<string> Checks { get; } = [];
}

public sealed class HealthReport
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public int Score { get; init; }
    public IReadOnlyList<HealthCategory> Categories { get; init; } = [];
    public IReadOnlyList<HealthIssue> Issues { get; init; } = [];
    public TimeSpan Duration { get; init; }

    public string ScoreLabel => Score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 50 => "Needs attention",
        _ => "Poor"
    };
}

public sealed record HealthProgress(double Fraction, string Step);

// ───────────────────────────── Logs ─────────────────────────────

public enum LogSeverity { Debug, Info, Warning, Error, Critical }

public sealed class LogEntry
{
    public int LineNumber { get; init; }
    public DateTime? Timestamp { get; init; }
    public LogSeverity Severity { get; init; }
    public string Message { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed class LogGroup
{
    public string Signature { get; init; } = "";
    public LogSeverity Severity { get; set; }
    public int Count { get; set; }
    public string Sample { get; set; } = "";
    public DateTime? First { get; set; }
    public DateTime? Last { get; set; }
    public int FirstLine { get; set; }
}

public sealed class LogPattern
{
    public string Title { get; init; } = "";
    public string Explanation { get; init; } = "";
    public LogSeverity Severity { get; init; }
    public int Occurrences { get; init; }
    public string? Evidence { get; init; }
}

public sealed class TimelineBucket
{
    public DateTime Start { get; init; }
    public int Info { get; set; }
    public int Warning { get; set; }
    public int Error { get; set; }
    public int Critical { get; set; }
}

public sealed class LogAnalysisResult
{
    public string Source { get; init; } = "";
    public string Format { get; init; } = "";
    public long FileSize { get; init; }
    public int TotalLines { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<LogEntry> Entries { get; init; } = [];
    public IReadOnlyDictionary<LogSeverity, int> Counts { get; init; } = new Dictionary<LogSeverity, int>();
    public IReadOnlyList<LogGroup> Groups { get; init; } = [];
    public IReadOnlyList<LogPattern> Patterns { get; init; } = [];
    public IReadOnlyList<TimelineBucket> Timeline { get; init; } = [];
    public TimeSpan TimelineBucketSize { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public int TimestampedEntries { get; init; }
    public string Summary { get; init; } = "";
    public TimeSpan Elapsed { get; init; }

    public int Count(LogSeverity s) => Counts.TryGetValue(s, out var c) ? c : 0;
}

// ───────────────────────────── AI ─────────────────────────────

public enum ChatRole { User, Assistant }

public sealed record ChatTurn(ChatRole Role, string Text);

public sealed class AiResult
{
    public bool Success { get; init; }
    public string Text { get; init; } = "";
    public string? Error { get; init; }
    public string? StopReason { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
}

// ───────────────────────────── Notifications ─────────────────────────────

public enum NotificationLevel { Info, Success, Warning, Error }
