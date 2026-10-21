namespace PCCommandCenter.Core;

/// <summary>Outcome of a user-initiated operation, carrying a user-friendly message.</summary>
public sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message = "") => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);

    /// <summary>Maps common exceptions to a friendly, localized message and logs the details.</summary>
    public static OperationResult FromException(string action, Exception ex)
    {
        AppLog.Warn($"{action} failed", ex);
        return Fail(FriendlyError(ex));
    }

    public static string FriendlyError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => Loc.T("Access denied. Administrator privileges may be required."),
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 } => Loc.T("Access denied. Administrator privileges may be required."),
        InvalidOperationException { InnerException: System.ComponentModel.Win32Exception { NativeErrorCode: 5 } } => Loc.T("Access denied. Administrator privileges may be required."),
        FileNotFoundException or DirectoryNotFoundException => Loc.T("The file or folder no longer exists."),
        TimeoutException => Loc.T("The operation timed out."),
        OperationCanceledException => Loc.T("The operation was cancelled."),
        IOException io => io.Message,
        _ => ex.Message
    };
}
