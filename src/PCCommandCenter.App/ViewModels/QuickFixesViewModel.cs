using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class FixItem : ObservableObject
{
    public required QuickFix Fix { get; init; }
    public string Title => Loc.T(Fix.Title);
    public string Description => Loc.T(Fix.Description);
    public string Symptoms => Loc.T(Fix.Symptoms);
    public string Glyph => Fix.Glyph;
    public string Category => Loc.T(Fix.Category);
    public bool RequiresAdmin => Fix.RequiresAdmin;
    public bool LongRunning => Fix.LongRunning;
    public bool RestartRecommended => Fix.RestartRecommended;
    public bool IsModerate => Fix.Risk == FixRisk.Moderate;
    public bool IsReadOnly => Fix.Risk == FixRisk.ReadOnly;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _lastResult;
    [ObservableProperty] private bool? _lastSuccess;
    [ObservableProperty] private bool _isVisible = true;

    public bool Matches(string query) =>
        query.Length == 0
        || Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Symptoms.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Fix.CommandPreview.Contains(query, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class FixGroup : ObservableObject
{
    public required string Name { get; init; }
    public required IReadOnlyList<FixItem> Items { get; init; }
    [ObservableProperty] private bool _isVisible = true;
}

public sealed partial class QuickFixesViewModel : PageViewModel
{
    private readonly IQuickFixService _fixes;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly NavigationService _nav;
    private readonly StringBuilder _buffer = new();
    private readonly DispatcherTimer _flush;
    private CancellationTokenSource? _cts;

    public QuickFixesViewModel(IQuickFixService fixes, DialogService dialogs, INotificationService notify, NavigationService nav)
    {
        _fixes = fixes;
        _dialogs = dialogs;
        _notify = notify;
        _nav = nav;
        Groups = fixes.Fixes.Select(f => new FixItem { Fix = f }).GroupBy(f => f.Fix.Category)
                            .Select(g => new FixGroup { Name = Loc.T(g.Key), Items = g.ToList() }).ToList();
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _flush.Tick += (_, _) => { lock (_buffer) Output = _buffer.ToString(); };
        ApplyFilter();
    }

    public IReadOnlyList<FixGroup> Groups { get; }
    public bool IsElevated => AppInfo.IsElevated;

    public static IReadOnlyList<string> Modes { get; } = ["All", "Repairs", "Diagnostics"];
    [ObservableProperty] private string _mode = "All";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _countText = "";
    partial void OnModeChanged(string value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        // From the command palette: show just that card.
        if (parameter is string id && Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Fix.Id == id) is { } item)
        {
            Mode = "All";
            Search = item.Title;
        }
    }

    private void ApplyFilter()
    {
        var q = Search.Trim();
        int visible = 0;
        foreach (var g in Groups)
        {
            foreach (var i in g.Items)
            {
                i.IsVisible = (Mode == "All" || (Mode == "Diagnostics") == i.IsReadOnly) && i.Matches(q);
                if (i.IsVisible) visible++;
            }
            g.IsVisible = g.Items.Any(i => i.IsVisible);
        }
        int repairs = Groups.Sum(g => g.Items.Count(i => !i.IsReadOnly)), diags = Groups.Sum(g => g.Items.Count(i => i.IsReadOnly));
        CountText = q.Length == 0 && Mode == "All" ? Loc.F("{0} repairs · {1} diagnostics", repairs, diags) : Loc.F("{0} results", visible);
    }

    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _outputTitle = "";
    [ObservableProperty] private bool _hasOutput;
    [ObservableProperty] private bool _anyRunning;

    [RelayCommand]
    private async Task RunAsync(FixItem? item)
    {
        if (item == null || AnyRunning) return;
        var f = item.Fix;
        var details = new StringBuilder();
        details.AppendLine(Loc.T("Commands that will run:"));
        details.AppendLine(f.CommandPreview);
        if (f.RequiresAdmin && !AppInfo.IsElevated) details.AppendLine().Append(Loc.T("Windows will ask for administrator permission and show the progress in a separate window."));
        if (f.LongRunning) details.AppendLine().Append(Loc.T("This can take 10 to 30 minutes. You can keep using the PC."));
        if (f.RestartRecommended) details.AppendLine().Append(Loc.T("A restart is required afterwards."));

        // Read-only diagnostics change nothing, so they run straight away (admin ones still go through the UAC prompt).
        if (!item.IsReadOnly)
        {
            var ok = await _dialogs.ConfirmAsync(new ConfirmDialog
            {
                Title = Loc.F("Run \"{0}\"?", item.Title),
                Message = item.Description,
                Details = details.ToString().TrimEnd(),
                ConfirmText = Loc.T("Run fix"),
                Kind = f.Risk == FixRisk.Moderate ? DialogKind.Warning : DialogKind.Info,
            });
            if (!ok) return;
        }

        item.IsRunning = true;
        AnyRunning = true;
        lock (_buffer) _buffer.Clear().AppendLine("> " + f.CommandPreview.Replace("\n", "\n> ")).AppendLine();
        Output = "";
        OutputTitle = item.Title;
        HasOutput = true;
        _cts = new CancellationTokenSource();
        _flush.Start();
        try
        {
            var result = await _fixes.RunAsync(f, line => { lock (_buffer) _buffer.AppendLine(line); }, _cts.Token);
            item.LastSuccess = result.Success;
            item.LastResult = result.Message;
            lock (_buffer) { if (result.RunsExternally) _buffer.AppendLine(result.Message); Output = _buffer.ToString(); }
            _notify.Notify(result.Success ? NotificationLevel.Success : NotificationLevel.Warning, item.Title, result.Message);
        }
        finally
        {
            _flush.Stop();
            lock (_buffer) Output = _buffer.ToString();
            item.IsRunning = false;
            AnyRunning = false;
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void CopyOutput()
    {
        if (Output.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(Output);
            _notify.Notify(NotificationLevel.Success, Loc.T("Copied to clipboard"), OutputTitle);
        }
        catch (Exception ex) { AppLog.Warn("Clipboard copy failed", ex); }
    }

    [RelayCommand]
    private void SaveOutput()
    {
        if (Output.Length == 0) return;
        var name = string.Concat(OutputTitle.Split(Path.GetInvalidFileNameChars()));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Save output"),
            FileName = $"{name} - {Environment.MachineName} - {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            Filter = "Text (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, $"{OutputTitle}\r\n{Environment.MachineName} · {DateTime.Now:g}\r\n\r\n{Output}", new UTF8Encoding(true));
            _notify.Notify(NotificationLevel.Success, Loc.T("Saved"), dialog.FileName);
        }
        catch (Exception ex) { _notify.Notify(NotificationLevel.Error, Loc.T("Unable to save"), OperationResult.FriendlyError(ex)); }
    }

    [RelayCommand]
    private void CloseOutput()
    {
        if (AnyRunning) return;
        HasOutput = false;
    }

    [RelayCommand]
    private async Task CreateRestorePointAsync()
    {
        var ok = await _dialogs.ConfirmAsync(Loc.T("Create a system restore point?"),
            Loc.T("A restore point lets you undo system changes (drivers, updates, registry) from Windows Recovery if something goes wrong."),
            Loc.T("Create restore point"), DialogKind.Info, Loc.T("Requires administrator permission. Windows allows one automatic restore point every 24 hours."));
        if (!ok) return;
        AnyRunning = true;
        try
        {
            var r = await _fixes.CreateRestorePointAsync(CancellationToken.None);
            _notify.Notify(r.Success ? NotificationLevel.Success : NotificationLevel.Warning, Loc.T("Restore point"), r.Message);
            if (r.Output.Length > 0) { OutputTitle = Loc.T("Create restore point"); Output = r.Output; HasOutput = true; }
        }
        finally { AnyRunning = false; }
    }

    [RelayCommand]
    private void AskAi()
    {
        if (!HasOutput) return;
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.F("Quick fix: {0}", OutputTitle), Output, Loc.T("Explain the result of this repair. Did it work?")));
        _nav.Navigate(PageKey.Ai);
    }
}
