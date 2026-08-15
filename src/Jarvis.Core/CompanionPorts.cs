using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed record WorktimeChannelConfiguration(
    bool Enabled,
    string CliPath,
    string Profile,
    string? BoundUserId,
    string? BoundChatId);

internal abstract record WorktimeInboundEvent(
    string EventId,
    string SenderId,
    DateTimeOffset ReceivedAt);

internal sealed record WorktimeTextInboundEvent(
    string EventId,
    string SenderId,
    DateTimeOffset ReceivedAt,
    string ChatId,
    string MessageId,
    string Text) : WorktimeInboundEvent(EventId, SenderId, ReceivedAt);

internal sealed record WorktimeActionInboundEvent(
    string EventId,
    string SenderId,
    DateTimeOffset ReceivedAt,
    string CallbackToken,
    Guid CardId,
    Guid CommitmentId,
    int CommitmentVersion,
    WorktimeActionKind Action,
    DateTimeOffset? RestEndAt,
    int? RestMinutes = null) : WorktimeInboundEvent(EventId, SenderId, ReceivedAt);

internal sealed record WorktimeDeliveryResult(
    bool Success,
    string? PlatformMessageId = null,
    string? ErrorCode = null,
    string? Message = null);

internal interface IWorktimeChannel : IAsyncDisposable
{
    bool IsHealthy { get; }
    bool NeedsRestart { get; }
    string? LastError { get; }

    ValueTask ConfigureAsync(
        WorktimeChannelConfiguration configuration,
        Func<WorktimeInboundEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken);

    ValueTask<WorktimeDeliveryResult> SendAsync(
        MobileEscalationCard card,
        CancellationToken cancellationToken);

    ValueTask<WorktimeDeliveryResult> SendDailyReviewInvitationAsync(
        Guid sessionId,
        DateOnly reviewDate,
        bool followUp,
        CancellationToken cancellationToken);

    ValueTask<WorktimeDeliveryResult> SendTextAsync(
        string recipientOpenId,
        string text,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<bool> InvalidateAsync(
        Guid cardId,
        string platformMessageId,
        string resultText,
        CancellationToken cancellationToken);
}

internal sealed record AiTokenUsage(
    int InputTokens,
    int OutputTokens,
    int CacheHitInputTokens);

internal sealed record AiProviderRequest(
    AiRequestPurpose Purpose,
    string Text,
    string Model,
    int MaxOutputTokens,
    DateTimeOffset Now,
    SupervisionSnapshot? Supervision = null,
    AiReviewFacts? ReviewFacts = null,
    string? PersonaInstructions = null);

internal sealed record AiReviewFacts(
    AiReviewKind Kind,
    Guid SourceId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string FactsSummary,
    IReadOnlyList<DailyReviewAnswerView> DailyAnswers,
    IReadOnlyList<CommitmentReviewView> CommitmentReviews,
    CycleTrendView? CycleTrends,
    int FactItemCount);

internal sealed record AiReviewDraftPayload(
    string DraftText,
    IReadOnlyList<string> Observations,
    IReadOnlyList<string> SuggestedAdjustments);

internal sealed record AiProviderResult(
    bool Success,
    string? Text,
    AiTokenUsage Usage,
    string? ErrorCode = null,
    string? Message = null,
    NaturalLanguageOperationCandidate? Candidate = null,
    IReadOnlyList<string>? MissingInformation = null,
    AiReviewDraftPayload? ReviewDraft = null);

internal interface ICloudAiProvider
{
    decimal EstimateCostCny(AiProviderRequest request);

    ValueTask<AiProviderResult> CompleteAsync(
        AiProviderRequest request,
        string credential,
        CancellationToken cancellationToken);
}

internal interface IAiCredentialStore
{
    ValueTask SaveAsync(string provider, string secret, CancellationToken cancellationToken);
    ValueTask<string?> ReadAsync(string provider, CancellationToken cancellationToken);
    ValueTask DeleteAsync(string provider, CancellationToken cancellationToken);
}
