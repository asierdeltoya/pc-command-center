using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class CommandBlock : ObservableObject
{
    private const int MaxChars = 400_000;
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _err = new();
    private bool _dirty;

    public string Command { get; init; } = "";
    public string Prompt { get; init; } = "";
    public ShellKind Shell { get; init; }
    public DateTime Started { get; } = DateTime.Now;

    [ObservableProperty] private string _stdout = "";
    [ObservableProperty] private string _stderr = "";
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private int? _exitCode;
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private bool _truncated;

    public bool Succeeded => ExitCode == 0;
    public bool HasStdout => Stdout.Length > 0;
    public bool HasStderr => Stderr.Length > 0;
    partial void OnStdoutChanged(string value) => OnPropertyChanged(nameof(HasStdout));
    partial void OnStderrChanged(string value) => OnPropertyChanged(nameof(HasStderr));
    partial void OnExitCodeChanged(int? value) => OnPropertyChanged(nameof(Succeeded));

    public void Append(ShellOutputLine line)
    {
        lock (_out)
        {
            var sb = line.IsError ? _err : _out;
            if (sb.Length > MaxChars) { Truncated = true; return; }
            sb.AppendLine(line.Text);
            _dirty = true;
        }
    }

    public void Flush()
    {
        lock (_out)
        {
            if (!_dirty) return;
            _dirty = false;
            Stdout = _out.ToString().TrimEnd('\r', '\n');
            Stderr = _err.ToString().TrimEnd('\r', '\n');
        }
    }

    public string FullText => $"{Prompt}{Command}\n{Stdout}{(HasStderr ? "\n[stderr]\n" + Stderr : "")}\n[exit code {ExitCode?.ToString() ?? "n/a"}]";
}

public sealed partial class TerminalSession : ObservableObject
{
    public ShellKind Kind { get; init; }
    public string Title => Kind == ShellKind.Cmd ? "CMD" : "PowerShell";
    public string PromptPrefix => Kind == ShellKind.Cmd ? $"{Cwd}>" : $"PS {Cwd}> ";
    public ObservableCollection<CommandBlock> Blocks { get; } = [];
    public List<string> History { get; } = [];
    public int HistoryIndex { get; set; }
    [ObservableProperty] private string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _inputFromAi;
    partial void OnCwdChanged(string value) => OnPropertyChanged(nameof(PromptPrefix));
}

public sealed partial class TerminalViewModel : PageViewModel, IRecipient<RunInTerminalMessage>
{
    private readonly IShellService _shell;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly NavigationService _nav;
    private readonly DispatcherTimer _flush;
    private CancellationTokenSource? _cts;
    private CommandBlock? _running;

    public TerminalViewModel(IShellService shell, DialogService dialogs, INotificationService notify, NavigationService nav)
    {
        _shell = shell;
        _dialogs = dialogs;
        _notify = notify;
        _nav = nav;
        Sessions = [new TerminalSession { Kind = ShellKind.PowerShell }, new TerminalSession { Kind = ShellKind.Cmd }];
        _current = Sessions[0];
        _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _flush.Tick += (_, _) => { _running?.Flush(); ScrollTick++; };
        WeakReferenceMessenger.Default.Register(this);
    }

    public ObservableCollection<TerminalSession> Sessions { get; }

    [ObservableProperty] private TerminalSession _current;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _isRunning;
    [ObservableProperty] private int _scrollTick;

    public void Receive(RunInTerminalMessage message)
    {
        Ui(() =>
        {
            Current = Sessions.First(s => s.Kind == message.Shell);
            Current.Input = message.Command;
            Current.InputFromAi = message.FromAi;
            if (message.FromAi)
                _notify.Notify(NotificationLevel.Info, Loc.T("Command suggested by the AI"), Loc.T("Review it in the terminal and press Run to execute it."));
        });
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var session = Current;
        var command = session.Input.Trim();
        if (command.Length == 0) return;

        if (command is "cls" or "clear" or "Clear-Host") { session.Blocks.Clear(); session.Input = ""; return; }
        if (TryChangeDirectory(session, command)) return;

        var risk = _shell.Assess(session.Kind, command);
        bool fromAi = session.InputFromAi;
        if (risk.Risk != CommandRisk.Safe || fromAi)
        {
            var dialog = new ConfirmDialog
            {
                Title = risk.Risk == CommandRisk.Destructive ? Loc.T("Run a potentially destructive command?")
                      : risk.Risk == CommandRisk.Modifies ? Loc.T("This command can modify your system")
                      : Loc.T("Run the command suggested by the AI?"),
                Message = risk.Risk != CommandRisk.Safe ? risk.Reason : Loc.T("Make sure you understand what this command does before running it."),
                Details = $"{session.Title}: {command}",
                ConfirmText = Loc.T("Run command"),
                Kind = risk.Risk == CommandRisk.Destructive ? DialogKind.Danger : DialogKind.Warning,
                Acknowledge = risk.Risk == CommandRisk.Destructive ? Loc.T("I understand this command can delete data or change system settings") : null,
            };
            if (!await _dialogs.ConfirmAsync(dialog)) return;
        }

        session.InputFromAi = false;
        session.History.Remove(command);
        session.History.Add(command);
        session.HistoryIndex = session.History.Count;
        session.Input = "";

        var block = new CommandBlock { Command = command, Prompt = session.PromptPrefix, Shell = session.Kind };
        session.Blocks.Add(block);
        while (session.Blocks.Count > 60) session.Blocks.RemoveAt(0);

        IsRunning = true;
        _running = block;
        _cts = new CancellationTokenSource();
        _flush.Start();
        try
        {
            var result = await _shell.RunAsync(session.Kind, command, session.Cwd, block.Append, _cts.Token);
            block.Flush();
            block.ExitCode = result.ExitCode;
            block.Footer = result.StartError ?? (result.Cancelled
                ? Loc.F("Stopped after {0}", Fmt.Duration(result.Duration))
                : Loc.F("Exit code {0} · {1}", result.ExitCode, Fmt.Duration(result.Duration)));
            if (block.Truncated) block.Footer += " · " + Loc.T("output truncated");
        }
        catch (Exception ex)
        {
            AppLog.Error("Terminal command failed", ex);
            block.Footer = OperationResult.FriendlyError(ex);
        }
        finally
        {
            _flush.Stop();
            block.IsRunning = false;
            _running = null;
            IsRunning = false;
            ScrollTick++;
        }
    }

    private bool TryChangeDirectory(TerminalSession session, string command)
    {
        var m = System.Text.RegularExpressions.Regex.Match(command, @"^(?:cd|chdir|Set-Location|sl)(?:\s+/d)?(?:\s+(?<p>.+))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var drive = System.Text.RegularExpressions.Regex.Match(command, @"^(?<d>[a-zA-Z]:)\\?$");
        if (!m.Success && !drive.Success) return false;

        string? target = drive.Success ? drive.Groups["d"].Value + "\\" : m.Groups["p"].Success ? m.Groups["p"].Value.Trim().Trim('"', '\'') : null;
        var block = new CommandBlock { Command = command, Prompt = session.PromptPrefix, Shell = session.Kind };
        if (target == null)
        {
            block.Append(new ShellOutputLine(session.Cwd, false));
        }
        else
        {
            target = Environment.ExpandEnvironmentVariables(target.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            var full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(session.Cwd, target));
            if (Directory.Exists(full)) session.Cwd = full.TrimEnd('\\').Length == 2 ? full : full.TrimEnd('\\');
            else block.Append(new ShellOutputLine(Loc.F("The system cannot find the path specified: {0}", full), true));
        }
        block.Flush();
        block.IsRunning = false;
        block.ExitCode = block.HasStderr ? 1 : 0;
        block.Footer = Loc.F("Exit code {0}", block.ExitCode);
        session.Blocks.Add(block);
        session.History.Add(command);
        session.HistoryIndex = session.History.Count;
        session.Input = "";
        ScrollTick++;
        return true;
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand] private void Clear() => Current.Blocks.Clear();

    [RelayCommand]
    private void CopyOutput(CommandBlock? block)
    {
        block ??= Current.Blocks.LastOrDefault();
        if (block == null) return;
        try { Clipboard.SetText(block.FullText); _notify.Notify(NotificationLevel.Success, Loc.T("Copied to clipboard"), block.Command); }
        catch { }
    }

    [RelayCommand]
    private void SendToAi(CommandBlock? block)
    {
        block ??= Current.Blocks.LastOrDefault();
        if (block == null) return;
        var text = block.FullText;
        if (text.Length > 60_000) text = text[..30_000] + "\n…\n" + text[^30_000..];
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.F("Terminal: {0}", block.Command), text,
            Loc.T("Explain this command output. Is there a problem?")));
        _nav.Navigate(PageKey.Ai);
    }

    public void HistoryUp()
    {
        var s = Current;
        if (s.History.Count == 0) return;
        s.HistoryIndex = Math.Max(0, s.HistoryIndex - 1);
        s.Input = s.History[s.HistoryIndex];
    }

    public void HistoryDown()
    {
        var s = Current;
        if (s.History.Count == 0) return;
        s.HistoryIndex = Math.Min(s.History.Count, s.HistoryIndex + 1);
        s.Input = s.HistoryIndex < s.History.Count ? s.History[s.HistoryIndex] : "";
    }

    [RelayCommand] private void Rerun(CommandBlock? block) { if (block != null && !IsRunning) { Current.Input = block.Command; _ = RunAsync(); } }
}
