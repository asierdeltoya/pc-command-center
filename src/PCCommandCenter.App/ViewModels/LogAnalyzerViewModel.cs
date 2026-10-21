using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed class LogGroupItem
{
    public required LogGroup Group { get; init; }
    public LogSeverity Severity => Group.Severity;
    public string SeverityText => Group.Severity.ToString().ToUpperInvariant();
    public int Count => Group.Count;
    public string Sample => Group.Sample.Length > 300 ? Group.Sample[..300] + "…" : Group.Sample;
    public string Range => Group.First is { } f
        ? (Group.Last is { } l && l != f ? $"{f:g} → {l:g}" : f.ToString("g"))
        : Loc.F("first at line {0}", Group.FirstLine);
}

public sealed class LogEntryItem
{
    public required LogEntry Entry { get; init; }
    public int Line => Entry.LineNumber;
    public string Time => Entry.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
    public LogSeverity Severity => Entry.Severity;
    public string SeverityText => Entry.Severity.ToString().ToUpperInvariant();
    public string Message => Entry.Message.Length > 500 ? Entry.Message[..500] + "…" : Entry.Message;
}

public sealed partial class LogAnalyzerViewModel : PageViewModel
{
    private readonly ILogAnalyzer _analyzer;
    private readonly INotificationService _notify;
    private readonly NavigationService _nav;
    private CancellationTokenSource? _cts;
    private LogAnalysisResult? _result;
    private List<LogEntryItem> _allEntries = [];

    public LogAnalyzerViewModel(ILogAnalyzer analyzer, INotificationService notify, NavigationService nav)
    {
        _analyzer = analyzer;
        _notify = notify;
        _nav = nav;
    }

    public ObservableCollection<LogPattern> Patterns { get; } = [];
    public ObservableCollection<LogGroupItem> Groups { get; } = [];
    public ObservableCollection<LogEntryItem> Entries { get; } = [];

    public static IReadOnlyList<string> SeverityFilters { get; } = ["All", "Critical", "Error", "Warning", "Info", "Debug"];

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _fileInfo = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _critical;
    [ObservableProperty] private int _errors;
    [ObservableProperty] private int _warnings;
    [ObservableProperty] private int _infos;
    [ObservableProperty] private string _patternHeadline = "";
    [ObservableProperty] private IReadOnlyList<TimelineBucket> _timeline = [];
    [ObservableProperty] private bool _hasTimeline;
    [ObservableProperty] private string _timelineCaption = "";
    [ObservableProperty] private string _severityFilter = "All";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _entriesCaption = "";
    [ObservableProperty] private string _pasteText = "";
    [ObservableProperty] private bool _isDragOver;

    public string SupportedText => string.Join("  ", _analyzer.SupportedExtensions.Take(8));

    partial void OnSeverityFilterChanged(string value) => ApplyEntryFilter();
    partial void OnSearchChanged(string value) => ApplyEntryFilter();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Choose a log file"),
            Filter = Loc.T("Log files") + " (*.log;*.txt;*.csv;*.tsv;*.json;*.jsonl;*.evtx;*.out;*.err)|*.log;*.txt;*.csv;*.tsv;*.json;*.jsonl;*.evtx;*.out;*.err|" + Loc.T("All files") + " (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true) await AnalyzeFileAsync(dialog.FileName);
    }

    [RelayCommand]
    private async Task OpenSampleAsync(string which)
    {
        // Real Windows logs that exist on most machines.
        var path = which switch
        {
            "cbs" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Logs\CBS\CBS.log"),
            "dism" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Logs\DISM\dism.log"),
            "app" => AppInfo.LogFile,
            _ => "",
        };
        if (File.Exists(path)) await AnalyzeFileAsync(path);
        else _notify.Notify(NotificationLevel.Info, Loc.T("File not found"), path);
    }

    public async Task AnalyzeFileAsync(string path)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        FileName = Path.GetFileName(path);
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            var result = await _analyzer.AnalyzeFileAsync(path, progress, _cts.Token);
            Apply(result);
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            _notify.Notify(NotificationLevel.Error, Loc.T("Unable to read the file"), Loc.T("Access denied. Administrator privileges may be required."));
        }
        catch (Exception ex)
        {
            AppLog.Error("Log analysis failed", ex);
            _notify.Notify(NotificationLevel.Error, Loc.T("Unable to analyze the file"), OperationResult.FriendlyError(ex));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AnalyzePastedAsync()
    {
        if (string.IsNullOrWhiteSpace(PasteText)) return;
        IsBusy = true;
        try { Apply(await _analyzer.AnalyzeTextAsync(PasteText, Loc.T("Pasted text"), CancellationToken.None)); FileName = Loc.T("Pasted text"); }
        catch (Exception ex) { AppLog.Error("Log analysis failed", ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    private void Apply(LogAnalysisResult r)
    {
        _result = r;
        HasResult = true;
        FileInfo = Loc.F("{0} · {1} · {2:N0} lines · analyzed in {3}", Loc.T(r.Format), Fmt.Bytes(r.FileSize), r.TotalLines, Fmt.Duration(r.Elapsed));
        Summary = r.Summary;
        Critical = r.Count(LogSeverity.Critical);
        Errors = r.Count(LogSeverity.Error);
        Warnings = r.Count(LogSeverity.Warning);
        Infos = r.Count(LogSeverity.Info) + r.Count(LogSeverity.Debug);
        int important = r.Patterns.Count(p => p.Severity >= LogSeverity.Warning);
        PatternHeadline = r.Patterns.Count == 0 ? Loc.T("No significant patterns detected")
            : Loc.F("{0} important patterns detected", important > 0 ? important : r.Patterns.Count);
        Patterns.Clear();
        foreach (var p in r.Patterns) Patterns.Add(p);
        Groups.Clear();
        foreach (var g in r.Groups.Take(100)) Groups.Add(new LogGroupItem { Group = g });
        Timeline = r.Timeline;
        HasTimeline = r.Timeline.Count > 1;
        TimelineCaption = HasTimeline ? Loc.F("{0:g} → {1:g} · one bar = {2}", r.From, r.To, Fmt.Duration(r.TimelineBucketSize))
                                      : Loc.T("No timestamps were recognized, so a timeline is not available.");
        _allEntries = r.Entries.Select(e => new LogEntryItem { Entry = e }).ToList();
        SeverityFilter = Errors + Critical > 0 ? "Error" : "All";
        ApplyEntryFilter();
    }

    private void ApplyEntryFilter()
    {
        IEnumerable<LogEntryItem> q = _allEntries;
        if (SeverityFilter == "Error") q = q.Where(e => e.Severity >= LogSeverity.Error);
        else if (SeverityFilter != "All" && Enum.TryParse<LogSeverity>(SeverityFilter, out var sev)) q = q.Where(e => e.Severity == sev);
        if (!string.IsNullOrWhiteSpace(Search)) q = q.Where(e => e.Entry.Message.Contains(Search, StringComparison.OrdinalIgnoreCase));
        var list = q.Take(5000).ToList();
        Entries.Clear();
        foreach (var e in list) Entries.Add(e);
        EntriesCaption = list.Count == 5000 ? Loc.T("Showing the first 5,000 matching entries") : Loc.F("{0:N0} entries", list.Count);
    }

    [RelayCommand]
    private void CopySummary()
    {
        try { Clipboard.SetText(BuildReport(false)); _notify.Notify(NotificationLevel.Success, Loc.T("Copied to clipboard"), Loc.T("Analysis summary")); }
        catch { }
    }

    private string BuildReport(bool forAi)
    {
        if (_result is not { } r) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"File: {r.Source} ({r.Format}, {Fmt.Bytes(r.FileSize)}, {r.TotalLines} lines)");
        sb.AppendLine($"CRITICAL {Critical} | ERROR {Errors} | WARNING {Warnings} | INFO {Infos}");
        if (r.From.HasValue) sb.AppendLine($"Time range: {r.From:g} → {r.To:g}");
        sb.AppendLine(r.Summary);
        sb.AppendLine();
        sb.AppendLine("Detected patterns:");
        foreach (var p in r.Patterns) sb.AppendLine($"- [{p.Severity}] {p.Title}: {p.Explanation}{(p.Evidence != null ? $" Example: {p.Evidence}" : "")}");
        sb.AppendLine();
        sb.AppendLine("Top message groups:");
        foreach (var g in r.Groups.Take(forAi ? 25 : 15)) sb.AppendLine($"- {g.Count}× [{g.Severity}] {g.Sample}");
        if (forAi)
        {
            sb.AppendLine();
            sb.AppendLine("Most recent error lines:");
            foreach (var e in r.Entries.Where(e => e.Severity >= LogSeverity.Error).TakeLast(30))
                sb.AppendLine($"L{e.LineNumber} {e.Timestamp:yyyy-MM-dd HH:mm:ss} [{e.Severity}] {(e.Message.Length > 300 ? e.Message[..300] : e.Message)}");
        }
        return sb.ToString();
    }

    [RelayCommand]
    private void SendToAi()
    {
        if (_result == null) return;
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.F("Log analysis: {0}", FileName), BuildReport(true),
            Loc.T("Analyze this log. What is the root cause and what should I do?")));
        _nav.Navigate(PageKey.Ai);
    }

    [RelayCommand]
    private void Clear()
    {
        HasResult = false;
        _result = null;
        _allEntries = [];
        Entries.Clear(); Groups.Clear(); Patterns.Clear();
    }
}
