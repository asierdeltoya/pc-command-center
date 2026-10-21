using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Controls;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class ChatMessage : ObservableObject
{
    public ChatRole Role { get; init; }
    public bool IsUser => Role == ChatRole.User;
    public DateTime Time { get; init; } = DateTime.Now;
    public string TimeText => Time.ToString("t");
    /// <summary>Text actually sent to the model (includes attachments) — may differ from what is displayed.</summary>
    public string? ApiText { get; init; }
    public IReadOnlyList<string> AttachmentTitles { get; init; } = [];
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _footer;
}

public sealed class AttachmentItem
{
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string Preview => Content.Length > 160 ? Content[..160].Replace('\n', ' ') + "…" : Content.Replace('\n', ' ');
    public string Size => Loc.F("{0:N0} characters", Content.Length);
}

public sealed partial class AiAssistantViewModel : PageViewModel, IRecipient<AiAttachmentMessage>
{
    private readonly IAiAssistant _ai;
    private readonly AiContextBuilder _context;
    private readonly ISettingsService _settings;
    private readonly NavigationService _nav;
    private readonly INotificationService _notify;
    private CancellationTokenSource? _cts;

    public AiAssistantViewModel(IAiAssistant ai, AiContextBuilder context, ISettingsService settings, NavigationService nav, INotificationService notify)
    {
        _ai = ai;
        _context = context;
        _settings = settings;
        _nav = nav;
        _notify = notify;
        IncludeSystem = settings.Current.AiIncludeSystemContext;
        IncludeProcesses = settings.Current.AiIncludeSystemContext;
        IncludeNetwork = settings.Current.AiIncludeSystemContext;
        IncludeHealth = settings.Current.AiIncludeSystemContext;
        WeakReferenceMessenger.Default.Register(this);
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];

    public IReadOnlyList<string> Suggestions { get; } =
    [
        Loc.T("Why is my PC slow?"),
        Loc.T("Which process is using so much RAM?"),
        Loc.T("Why is my latency so high?"),
        Loc.T("Is my storage healthy?"),
        Loc.T("Review my system health and prioritize fixes."),
    ];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _input = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isGenerating;
    [ObservableProperty] private bool _includeSystem;
    [ObservableProperty] private bool _includeProcesses;
    [ObservableProperty] private bool _includeNetwork;
    [ObservableProperty] private bool _includeHealth;
    [ObservableProperty] private string _lastContext = "";
    [ObservableProperty] private bool _contextPreviewOpen;
    [ObservableProperty] private int _scrollTick;

    public bool IsConfigured => _ai.IsConfigured;
    public string ModelName => _ai.ModelName;
    public bool HasMessages => Messages.Count > 0;

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(ModelName));
        if (parameter is string prompt && !string.IsNullOrWhiteSpace(prompt)) Input = prompt;
    }

    public void Receive(AiAttachmentMessage message)
    {
        Ui(() =>
        {
            Attachments.Add(new AttachmentItem { Title = message.Title, Content = message.Content });
            if (string.IsNullOrWhiteSpace(Input) && message.SuggestedPrompt != null) Input = message.SuggestedPrompt;
        });
    }

    [RelayCommand] private void RemoveAttachment(AttachmentItem? a) { if (a != null) Attachments.Remove(a); }

    [RelayCommand] private void UseSuggestion(string text) { Input = text; _ = SendAsync(); }

    private bool CanSend() => !IsGenerating && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!CanSend()) return;
        if (!_ai.IsConfigured)
        {
            _notify.Notify(NotificationLevel.Warning, Loc.T("AI is not configured"), Loc.T("Add your Anthropic API key in Settings → AI."));
            return;
        }

        var question = Input.Trim();
        var apiText = new StringBuilder(question);
        var titles = Attachments.Select(a => a.Title).ToList();
        foreach (var a in Attachments)
            apiText.Append("\n\n<attachment title=\"").Append(a.Title).Append("\">\n").Append(a.Content).Append("\n</attachment>");
        Attachments.Clear();
        Input = "";

        Messages.Add(new ChatMessage { Role = ChatRole.User, Text = question, ApiText = apiText.ToString(), AttachmentTitles = titles });
        var reply = new ChatMessage { Role = ChatRole.Assistant, IsStreaming = true };
        Messages.Add(reply);
        OnPropertyChanged(nameof(HasMessages));
        IsGenerating = true;
        ScrollTick++;

        _cts = new CancellationTokenSource();
        try
        {
            string? systemData = null;
            if (IncludeSystem || IncludeProcesses || IncludeHealth || IncludeNetwork)
            {
                systemData = await _context.BuildAsync(IncludeSystem, IncludeProcesses, IncludeHealth, IncludeNetwork, _cts.Token);
                LastContext = systemData;
            }

            // Skip failed exchanges (an errored reply and the question that produced it) so roles keep alternating.
            var history = new List<ChatTurn>();
            for (int i = 0; i < Messages.Count; i++)
            {
                var m = Messages[i];
                if (m == reply || m.Error != null || string.IsNullOrEmpty(m.Text)) continue;
                if (m.IsUser && i + 1 < Messages.Count && Messages[i + 1] != reply && Messages[i + 1].Error != null) continue;
                history.Add(new ChatTurn(m.Role, m.ApiText ?? m.Text));
            }

            var buffer = new StringBuilder();
            var result = await _ai.StreamReplyAsync(history, systemData, delta =>
            {
                lock (buffer) buffer.Append(delta);
                Ui(() =>
                {
                    lock (buffer) reply.Text = buffer.ToString();
                    ScrollTick++;
                });
            }, _cts.Token);

            reply.Text = result.Text.Length > 0 ? result.Text : reply.Text;
            if (result.Error != null)
            {
                // The failed reply stays visible but is excluded from future history (Error != null).
                reply.Error = result.Error;
            }
            else
            {
                reply.Footer = result.StopReason == "cancelled"
                    ? Loc.T("Stopped")
                    : Loc.F("{0} · {1:N0} in / {2:N0} out tokens", _ai.ModelName, result.InputTokens, result.OutputTokens);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("AI send failed", ex);
            reply.Error = OperationResult.FriendlyError(ex);
        }
        finally
        {
            reply.IsStreaming = false;
            IsGenerating = false;
            ScrollTick++;
        }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        _cts?.Cancel();
        Messages.Clear();
        Attachments.Clear();
        LastContext = "";
        OnPropertyChanged(nameof(HasMessages));
    }

    [RelayCommand] private void OpenSettings() => _nav.Navigate(PageKey.Settings, "ai");

    [RelayCommand] private void ToggleContextPreview() => ContextPreviewOpen = !ContextPreviewOpen;

    [RelayCommand]
    private async Task PreviewContextAsync()
    {
        LastContext = await _context.BuildAsync(IncludeSystem, IncludeProcesses, IncludeHealth, IncludeNetwork, CancellationToken.None);
        ContextPreviewOpen = true;
    }

    [RelayCommand]
    private void CopyCode(CodeBlock? block)
    {
        if (block == null) return;
        try { Clipboard.SetText(block.Code); _notify.Notify(NotificationLevel.Success, Loc.T("Copied to clipboard"), Loc.T("Command copied")); }
        catch { }
    }

    /// <summary>Hands the command to the terminal pre-filled; it never runs without the user pressing Run (and confirming if risky).</summary>
    [RelayCommand]
    private void RunCode(CodeBlock? block)
    {
        if (block == null) return;
        var shell = block.Language is "cmd" or "bat" or "batch" ? ShellKind.Cmd : ShellKind.PowerShell;
        WeakReferenceMessenger.Default.Send(new RunInTerminalMessage(shell, block.Code.Trim(), FromAi: true));
        _nav.Navigate(PageKey.Terminal);
    }

    [RelayCommand]
    private void CopyMessage(ChatMessage? m)
    {
        if (m == null) return;
        try { Clipboard.SetText(m.Text); } catch { }
    }
}
