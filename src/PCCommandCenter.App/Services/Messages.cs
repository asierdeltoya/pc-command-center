using PCCommandCenter.Core;

namespace PCCommandCenter.App.Services;

/// <summary>Context handed to the AI assistant from another module (terminal output, log analysis, process…).</summary>
public sealed record AiAttachmentMessage(string Title, string Content, string? SuggestedPrompt = null);

/// <summary>Asks the terminal to pre-fill (never auto-run) a command.</summary>
public sealed record RunInTerminalMessage(ShellKind Shell, string Command, bool FromAi);

/// <summary>Opens a specific tool inside the Network page, e.g. ("ping", "8.8.8.8").</summary>
public sealed record NetworkToolRequest(string Tool, string? Target = null);
