using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Services;

public enum DialogKind { Info, Warning, Danger }

/// <summary>An in-window modal confirmation. Nothing destructive happens unless the user confirms it.</summary>
public sealed partial class ConfirmDialog : ObservableObject
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Details { get; init; }
    public string ConfirmText { get; init; } = Loc.T("Confirm");
    public string CancelText { get; init; } = Loc.T("Cancel");
    public DialogKind Kind { get; init; } = DialogKind.Warning;
    /// <summary>When set, the user must tick a checkbox with this text before confirming.</summary>
    public string? Acknowledge { get; init; }

    public string Glyph => Kind switch { DialogKind.Danger => "", DialogKind.Warning => "", _ => "" };
    public bool IsDanger => Kind == DialogKind.Danger;
    public bool HasAcknowledge => !string.IsNullOrEmpty(Acknowledge);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _acknowledged;

    public Task<bool> Result => _tcs.Task;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => _tcs.TrySetResult(true);

    private bool CanConfirm() => !HasAcknowledge || Acknowledged;

    [RelayCommand]
    private void Cancel() => _tcs.TrySetResult(false);
}

public sealed partial class DialogService : ObservableObject
{
    [ObservableProperty] private ConfirmDialog? _current;

    public async Task<bool> ConfirmAsync(ConfirmDialog dialog)
    {
        // Serialize dialogs: wait for any open one to close first.
        while (Current != null) await Current.Result;
        Current = dialog;
        try { return await dialog.Result; }
        finally { Current = null; }
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, DialogKind kind = DialogKind.Warning, string? details = null, string? acknowledge = null) =>
        ConfirmAsync(new ConfirmDialog { Title = title, Message = message, ConfirmText = confirmText, Kind = kind, Details = details, Acknowledge = acknowledge });
}
