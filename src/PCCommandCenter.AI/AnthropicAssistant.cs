using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.AI;

/// <summary>
/// Technical assistant backed by Claude through the official Anthropic SDK. The API key is read from the
/// DPAPI-protected secret store; system data is only included when the user's privacy settings allow it.
/// </summary>
public sealed class AnthropicAssistant : IAiAssistant
{
    public const string ApiKeySecret = "anthropic-api-key";

    public static IReadOnlyList<(string Id, string Label)> Models { get; } =
    [
        ("claude-opus-5-5", "Claude Opus 5.5 — most capable (default)"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5 — fast and capable"),
        ("claude-haiku-5-5", "Claude Haiku 5.5 — fastest, lowest cost"),
    ];

    public static IReadOnlyList<string> EffortLevels { get; } = ["low", "medium", "high", "xhigh"];

    private const string SystemPrompt = """
        You are the technical assistant built into "PC Command Center", a Windows diagnostics application.
        You help the user understand performance problems, errors, logs, network issues and processes on THEIR computer.

        Ground rules:
        - The application may attach a SYSTEM DATA block with live measurements taken from Windows (performance counters,
          WMI, NVML, iphlpapi, event logs, etc.). Treat it as the only source of truth about this computer.
        - Never invent measurements, process names, hardware, errors or log lines. If something you would need is not in the
          provided data, say plainly that it is not available and, if useful, explain how the user can obtain it.
        - Values reported as "N/A" or "Not available" could not be measured; do not guess them.
        - Keep answers concise, practical and specific to the data provided.

        Structure every diagnostic answer with these sections (omit a section only when it would be empty):
        ### ✅ Verified
        Facts taken directly from the attached system data or the user's input, quoting the relevant numbers.
        ### 💡 Hypotheses
        Likely explanations that are NOT confirmed by the data, each with a short reason and how to confirm it.
        ### 🛠 Recommendations
        Concrete, prioritized next steps. Prefer safe, reversible actions first.

        Commands:
        - When suggesting a command, put it in a fenced code block tagged `powershell` or `cmd`, one command per block.
        - Before any command that changes the system (deletes files, changes services, registry, network, power state,
          installs software), write a line starting with "⚠️ Modifies the system:" explaining the effect.
        - The user runs commands manually from the integrated terminal, which asks for confirmation; never claim you ran anything.

        Answer in the same language the user writes in.
        """;

    private readonly ISecretStore _secrets;
    private readonly ISettingsService _settings;

    public AnthropicAssistant(ISecretStore secrets, ISettingsService settings)
    {
        _secrets = secrets;
        _settings = settings;
    }

    public bool IsConfigured => _secrets.Has(ApiKeySecret);

    public string ModelName => Models.FirstOrDefault(m => m.Id == _settings.Current.AiModel).Label?.Split(" — ")[0] ?? _settings.Current.AiModel;

    private AnthropicClient CreateClient() => new()
    {
        ApiKey = _secrets.Get(ApiKeySecret),
        Timeout = TimeSpan.FromMinutes(10),
        MaxRetries = 2,
    };

    public async Task<AiResult> StreamReplyAsync(IReadOnlyList<ChatTurn> conversation, string? systemContext, Action<string> onTextDelta, CancellationToken ct)
    {
        if (!IsConfigured)
            return new AiResult { Error = Loc.T("AI is not configured. Add your Anthropic API key in Settings → AI.") };

        var messages = new List<BetaMessageParam>();
        for (int i = 0; i < conversation.Count; i++)
        {
            var turn = conversation[i];
            var text = turn.Text;
            // System data is attached to the latest user message so earlier turns stay byte-identical (cache friendly).
            if (i == conversation.Count - 1 && turn.Role == ChatRole.User && !string.IsNullOrWhiteSpace(systemContext))
                text = $"<system_data captured=\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\">\n{systemContext}\n</system_data>\n\n{text}";
            messages.Add(new BetaMessageParam { Role = turn.Role == ChatRole.User ? Role.User : Role.Assistant, Content = text });
        }

        var parameters = new MessageCreateParams
        {
            Model = _settings.Current.AiModel,
            MaxTokens = 32000,
            System = SystemPrompt,
            Messages = messages,
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_settings.Current.AiEffort) },
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(), // server-defined refusal fallback for the selected model
        };

        var full = new System.Text.StringBuilder();
        string? stopReason = null;
        long inputTokens = 0, outputTokens = 0;
        try
        {
            using var client = CreateClient();
            await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, ct).WithCancellation(ct))
            {
                if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    full.Append(text.Text);
                    onTextDelta(text.Text);
                }
                else if (ev.TryPickDelta(out var messageDelta))
                {
                    stopReason = messageDelta.Delta.StopReason?.ToString();
                    outputTokens = messageDelta.Usage.OutputTokens;
                }
                else if (ev.TryPickStart(out var start))
                {
                    inputTokens = start.Message.Usage.InputTokens;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new AiResult { Success = full.Length > 0, Text = full.ToString(), StopReason = "cancelled" };
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI request failed", ex);
            return new AiResult { Text = full.ToString(), Error = Describe(ex) };
        }

        if (stopReason != null && stopReason.Contains("refusal", StringComparison.OrdinalIgnoreCase) && full.Length == 0)
            return new AiResult { StopReason = stopReason, Error = Loc.T("The model declined to answer this request.") };

        return new AiResult
        {
            Success = true,
            Text = full.ToString(),
            StopReason = stopReason,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
        };
    }

    public async Task<OperationResult> TestConnectionAsync(CancellationToken ct)
    {
        if (!IsConfigured) return OperationResult.Fail(Loc.T("No API key saved."));
        try
        {
            using var client = CreateClient();
            var model = await client.Models.Retrieve(_settings.Current.AiModel, cancellationToken: ct).ConfigureAwait(false);
            return OperationResult.Ok(Loc.F("Connected — {0} is available", model.DisplayName));
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI connection test failed", ex);
            return OperationResult.Fail(Describe(ex));
        }
    }

    private static Effort ParseEffort(string value) => value switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    private static string Describe(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => Loc.T("The API key was rejected. Check it in Settings → AI."),
        AnthropicForbiddenException => Loc.T("This API key does not have access to the selected model."),
        AnthropicNotFoundException => Loc.T("The selected model was not found for this API key."),
        AnthropicRateLimitException => Loc.T("Rate limit reached. Wait a moment and try again."),
        Anthropic5xxException => Loc.T("The AI service is temporarily unavailable. Try again shortly."),
        AnthropicBadRequestException bad => Loc.F("The request was rejected: {0}", bad.Message),
        AnthropicIOException or HttpRequestException => Loc.T("Unable to reach the AI service. Check your internet connection."),
        _ => Loc.F("AI error: {0}", ex.Message),
    };
}
