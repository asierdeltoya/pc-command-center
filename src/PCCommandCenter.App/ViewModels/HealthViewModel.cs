using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed class HealthCategoryItem
{
    public required HealthCategory Model { get; init; }
    public string Name => Model.Name;
    public HealthStatus Status => Model.Status;
    public string StatusText => Model.Status switch
    {
        HealthStatus.Good => Loc.T("GOOD"),
        HealthStatus.Warning => Loc.T("WARNING"),
        HealthStatus.Critical => Loc.T("CRITICAL"),
        _ => Loc.T("NOT AVAILABLE"),
    };
    public string Summary => Model.Summary;
    public IReadOnlyList<string> Checks => Model.Checks;
    public string Glyph => Model.Key switch
    {
        "cpu" => "",
        "gpu" => "",
        "memory" => "",
        "storage" => "",
        "network" => "",
        "startup" => "",
        "windows" => "",
        "security" => ((char)0xEA18).ToString(),
        "battery" => ((char)0xE83F).ToString(),
        _ => "",
    };
}

public sealed class HealthIssueItem
{
    public required HealthIssue Model { get; init; }
    public IssueSeverity Severity => Model.Severity;
    public string Title => Model.Title;
    public string Detail => Model.Detail;
    public string Recommendation => Model.Recommendation;
    public string Category => Loc.T(Model.Category);
    public bool HasAction => Model.Action != IssueAction.None;
    public string Glyph => Model.Severity switch { IssueSeverity.Critical => "", IssueSeverity.Warning => "", _ => "" };
    public string ActionText => Model.Action switch
    {
        IssueAction.OpenProcesses => Loc.T("Open Processes"),
        IssueAction.OpenStorage => Loc.T("Open Storage Analyzer"),
        IssueAction.OpenStartup => Loc.T("Open Startup"),
        IssueAction.OpenNetwork => Loc.T("Open Network"),
        IssueAction.OpenWindows => Loc.T("Open Windows tools"),
        IssueAction.OpenSecurity => Loc.T("Open Security"),
        IssueAction.OpenFixes => Loc.T("Open Repair & diagnose"),
        IssueAction.OpenApps => Loc.T("Open Apps"),
        _ => Loc.T("Open"),
    };
}

public sealed partial class HealthViewModel : PageViewModel
{
    private readonly IHealthService _health;
    private readonly NavigationService _nav;
    private readonly INotificationService _notify;
    private readonly IReportService _reports;
    private CancellationTokenSource? _cts;

    public HealthViewModel(IHealthService health, NavigationService nav, INotificationService notify, IReportService reports)
    {
        _reports = reports;
        _health = health;
        _nav = nav;
        _notify = notify;
        _health.ReportUpdated += (_, r) => Ui(() => Apply(r));
        if (_health.LastReport != null) Apply(_health.LastReport);
    }

    public ObservableCollection<HealthCategoryItem> Categories { get; } = [];
    public ObservableCollection<HealthIssueItem> Issues { get; } = [];

    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private int _score;
    [ObservableProperty] private string _scoreLabel = "";
    [ObservableProperty] private HealthStatus _overall = HealthStatus.NotAvailable;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _step = "";
    [ObservableProperty] private string _lastScan = "";
    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (parameter is "report") { _ = ExportReportAsync(); return; }
        if (parameter is "scan" || (!HasReport && !IsScanning)) _ = ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        Progress = 0;
        Step = Loc.T("Starting…");
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<HealthProgress>(p => { Progress = p.Fraction; Step = p.Step; });
            var report = await _health.RunScanAsync(progress, _cts.Token);
            Apply(report);
            _notify.Notify(NotificationLevel.Success, Loc.T("System scan completed"), Loc.F("Health score: {0}/100", report.Score));
        }
        catch (OperationCanceledException) { Step = Loc.T("Scan cancelled"); }
        catch (Exception ex)
        {
            AppLog.Error("Health scan failed", ex);
            _notify.Notify(NotificationLevel.Error, Loc.T("System scan failed"), OperationResult.FriendlyError(ex));
        }
        finally { IsScanning = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [ObservableProperty] private bool _exporting;

    /// <summary>Saves a complete HTML report (for a technician or for your records) and opens it.</summary>
    [RelayCommand]
    private async Task ExportReportAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Save system report"),
            FileName = $"PC-report-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}.html",
            Filter = "HTML (*.html)|*.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog() != true) return;
        Exporting = true;
        try
        {
            var path = await _reports.CreateHtmlReportAsync(dialog.FileName, CancellationToken.None);
            _notify.Notify(NotificationLevel.Success, Loc.T("Report saved"), path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Report export failed", ex);
            _notify.Notify(NotificationLevel.Error, Loc.T("Unable to create the report"), OperationResult.FriendlyError(ex));
        }
        finally { Exporting = false; }
    }

    private void Apply(HealthReport r)
    {
        HasReport = true;
        Score = r.Score;
        ScoreLabel = Loc.T(r.ScoreLabel);
        Overall = r.Score >= 85 ? HealthStatus.Good : r.Score >= 60 ? HealthStatus.Warning : HealthStatus.Critical;
        LastScan = Loc.F("Last scan {0} · took {1}", r.Timestamp.ToString("t"), Fmt.Duration(r.Duration));
        Categories.Clear();
        foreach (var c in r.Categories) Categories.Add(new HealthCategoryItem { Model = c });
        Issues.Clear();
        foreach (var i in r.Issues) Issues.Add(new HealthIssueItem { Model = i });
        CriticalCount = r.Issues.Count(i => i.Severity == IssueSeverity.Critical);
        WarningCount = r.Issues.Count(i => i.Severity == IssueSeverity.Warning);
        InfoCount = r.Issues.Count(i => i.Severity == IssueSeverity.Info);
    }

    [RelayCommand]
    private void OpenAction(HealthIssueItem? item)
    {
        if (item == null) return;
        var page = item.Model.Action switch
        {
            IssueAction.OpenProcesses => PageKey.Processes,
            IssueAction.OpenStorage => PageKey.Storage,
            IssueAction.OpenStartup => PageKey.Startup,
            IssueAction.OpenNetwork => PageKey.Network,
            IssueAction.OpenWindows => PageKey.Windows,
            IssueAction.OpenSecurity => PageKey.Security,
            IssueAction.OpenFixes => PageKey.Fixes,
            IssueAction.OpenApps => PageKey.Apps,
            _ => PageKey.Dashboard,
        };
        _nav.Navigate(page);
    }

    [RelayCommand]
    private void AskAi(HealthIssueItem? item)
    {
        if (item == null) return;
        var text = $"[{item.Model.Severity}] {item.Model.Category}: {item.Title}\n{item.Detail}\nApp recommendation: {item.Recommendation}";
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(item.Title, text, Loc.T("Explain this finding and what I should do about it.")));
        _nav.Navigate(PageKey.Ai);
    }

    [RelayCommand]
    private void AskAiReport()
    {
        if (_health.LastReport is not { } r) return;
        var lines = r.Categories.Select(c => $"{c.Name}: {c.Status} — {c.Summary}\n  " + string.Join("\n  ", c.Checks))
                    .Concat(r.Issues.Select(i => $"- [{i.Severity}] {i.Title}: {i.Detail}"));
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.F("Health report ({0}/100)", r.Score), string.Join("\n", lines),
            Loc.T("Review this health report and tell me what to fix first.")));
        _nav.Navigate(PageKey.Ai);
    }
}
