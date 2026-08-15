using System.Text.Json.Serialization;

namespace Jarvis.Contracts;

public enum WorktimeActionKind
{
    ReturnNow,
    StartRest,
    AdjustCommitment,
    Misclassification
}

public enum MobileCardState
{
    Active,
    Superseded,
    Responded,
    Cancelled,
    PendingDelivery,
    SupersedePending,
    CancellationPending,
    ResponsePending
}

public enum NotificationPreviewMode
{
    Privacy,
    Detailed
}

public enum CommitmentReviewState
{
    Pending,
    Deferred,
    Completed,
    Skipped
}

public enum CompletionAssessment
{
    Completed,
    Partial,
    NotCompleted
}

public enum ReviewSessionState
{
    NotDue,
    Pending,
    InProgress,
    Snoozed,
    Completed,
    Skipped,
    NoResponse
}

public enum ReviewQuestionKind
{
    Facts,
    PendingCommitments,
    WhatWentWell,
    WhatWentPoorly,
    Reasons,
    TomorrowAdjustments
}

public enum AiRequestPurpose
{
    BasicChat,
    NaturalLanguageOperation,
    DailyReviewAssist,
    CycleReviewAssist
}

public enum AiModelPreference
{
    Flash,
    Pro
}

public enum AiReviewKind
{
    Daily,
    Cycle
}

public enum AiReviewDraftState
{
    Pending,
    Confirmed,
    Discarded
}

public enum CandidateSource
{
    Desktop,
    Feishu
}

public enum CandidateOperationKind
{
    CreateCommitment,
    CreateFromTemplate,
    CreateRecurrence,
    ReviseCommitment,
    SaveTemplate,
    EndCommitmentEarly,
    CancelCommitment,
    DeferCommitment
}

public sealed record MobileEscalationCard(
    Guid CardId,
    Guid CommitmentId,
    int CommitmentVersion,
    int Sequence,
    DateTimeOffset SentAt,
    DateTimeOffset PlannedStartAt,
    DateTimeOffset PlannedEndAt,
    DateTimeOffset DeviationStartedAt,
    ActivityClassification Classification,
    string CommitmentSummary,
    string PrivacyPreview,
    MobileCardState State = MobileCardState.Active,
    string? PlatformMessageId = null,
    int DefaultRestMinutes = 15,
    string? InvalidationResultText = null,
    TimeSpan? CountedDeviation = null);

public sealed record WorktimeChannelView(
    bool Enabled,
    bool ListenerReady,
    bool UserBound,
    string? Profile,
    string? BoundUserSuffix,
    string? LastError,
    NotificationPreviewMode PreviewMode = NotificationPreviewMode.Privacy);

public sealed record CommitmentReviewView(
    Guid CommitmentId,
    int CommitmentVersion,
    CommitmentReviewState State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DeferredUntil = null,
    string? RawText = null,
    CompletionAssessment? Assessment = null,
    DateTimeOffset? AnsweredAt = null);

public sealed record DailyReviewAnswerView(
    ReviewQuestionKind Question,
    string RawText,
    DateTimeOffset AnsweredAt);

public sealed record DailyReviewView(
    ReviewSessionState State,
    TimeOnly ScheduledLocalTime,
    Guid? SessionId = null,
    DateOnly? ReviewDate = null,
    ReviewQuestionKind? CurrentQuestion = null,
    bool FollowUpUsed = false,
    bool MobileInviteSent = false,
    DateTimeOffset? SnoozedUntil = null,
    IReadOnlyList<string>? RawAnswers = null,
    IReadOnlyList<DailyReviewAnswerView>? StructuredAnswers = null,
    DateTimeOffset? InvitedAt = null,
    string FactsSummary = "")
{
    public IReadOnlyList<string> Answers { get; init; } = RawAnswers ?? [];
    public IReadOnlyList<DailyReviewAnswerView> AnswerDetails { get; init; } = StructuredAnswers ?? [];
}

public sealed record CycleCommitmentTraceView(
    Guid CommitmentId,
    DateOnly LocalDate,
    string? InputGoal,
    string? OutcomeGoal,
    double PlannedMinutes,
    double RelatedMinutes,
    double DistractingMinutes,
    double RestMinutes,
    CommitmentReviewState? ReviewState,
    CompletionAssessment? Assessment,
    string? ReviewText);

public sealed record DailyReviewTraceView(
    Guid SessionId,
    DateOnly ReviewDate,
    ReviewSessionState State,
    int AnswerCount);

public sealed record CycleTrendView(
    int PlannedCommitments,
    int ReviewedCommitments,
    double PlannedMinutes,
    double RelatedMinutes,
    double DistractingMinutes,
    double RestMinutes,
    int DeferredReviews,
    int NoResponseCount,
    double ObservedMinutes = 0,
    IReadOnlyList<CycleCommitmentTraceView>? CommitmentDetails = null,
    IReadOnlyList<DailyReviewTraceView>? DailyReviewDetails = null)
{
    [JsonIgnore]
    public IReadOnlyList<CycleCommitmentTraceView> Commitments => CommitmentDetails ?? [];
    [JsonIgnore]
    public IReadOnlyList<DailyReviewTraceView> DailyReviews => DailyReviewDetails ?? [];
}

public sealed record CycleReviewView(
    ReviewSessionState State,
    int IntervalDays,
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null,
    CycleTrendView? Trends = null,
    string Summary = "",
    IReadOnlyList<string>? Focuses = null)
{
    public IReadOnlyList<string> ConfirmedFocuses { get; init; } = Focuses ?? [];
}

public sealed record AiStatusView(
    bool Enabled,
    string Provider,
    string Model,
    string? CredentialLastFour,
    decimal MonthSpendCny,
    decimal MonthlyHardCapCny,
    bool Alert15Reached,
    bool Alert24Reached,
    string? LastError,
    bool IsRequestInProgress = false,
    AiModelPreference ModelPreference = AiModelPreference.Flash);

public sealed record AiRequestRecordView(
    Guid RequestId,
    DateTimeOffset RequestedAt,
    AiRequestPurpose Purpose,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    int CacheHitInputTokens,
    string PriceVersion,
    decimal CostCny,
    bool Success,
    int LatencyMilliseconds = 0);

public sealed record AiReviewEvaluationView(
    int QualityRating,
    bool StructureReliable,
    bool AmbiguityHandled,
    bool NoOverreach,
    bool PrivacyScopeConfirmed,
    string? Note = null);

public sealed record AiReviewDraftView(
    Guid DraftId,
    AiReviewKind Kind,
    Guid SourceId,
    Guid RequestId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateTimeOffset CreatedAt,
    AiReviewDraftState State,
    string Provider,
    string Model,
    string FactsScope,
    int FactItemCount,
    string DraftText,
    string? ConfirmedText = null,
    DateTimeOffset? ConfirmedAt = null,
    bool UserModified = false,
    AiReviewEvaluationView? Evaluation = null,
    string? AnonymizedComparisonPrompt = null);

public sealed record AiTrialEvidenceView(
    DateTimeOffset? TrialStartedAt,
    DateTimeOffset? TrialEndsAt,
    bool TrialWindowComplete,
    int TotalRequests,
    int SuccessfulRequests,
    int FailedRequests,
    int DailyRequests,
    int CycleRequests,
    int ConfirmedDrafts,
    int ModifiedDrafts,
    int ManualComparisonCount,
    double AverageLatencyMilliseconds,
    decimal TotalCostCny,
    double? AverageQualityRating,
    double? StructureReliableRate,
    double? AmbiguityHandledRate,
    double? NoOverreachRate,
    double? PrivacyScopeConfirmedRate,
    IReadOnlyList<string>? Models = null,
    double? ManualAverageQualityRating = null,
    double? ManualStructureReliableRate = null,
    double? ManualAmbiguityHandledRate = null,
    double? ManualNoOverreachRate = null)
{
    public static AiTrialEvidenceView Empty { get; } = new(
        null, null, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0m,
        null, null, null, null, null, []);

    [JsonIgnore]
    public IReadOnlyList<string> UsedModels => Models ?? [];
}

public sealed record ChatMessageView(
    Guid MessageId,
    DateTimeOffset At,
    string Role,
    string Text);

public sealed record CompanionPersonaSettingsView(
    bool ProfessionalMode,
    bool ProactiveEnabled,
    string? PreferredAddress,
    IReadOnlyList<string> DisallowedAddresses,
    string DislikedTone,
    string InteractionBoundary)
{
    public static CompanionPersonaSettingsView Default { get; } = new(
        false,
        true,
        null,
        [],
        "不使用生气、吃醋、冷落、羞辱、愧疚或失落追问",
        "不产生亲密度、照顾义务、关系承诺或情绪惩罚");
}

public sealed record ProactiveCompanionPromptView(
    Guid PromptId,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? PresentedAt = null);

public sealed record CompanionPersonaView(
    CompanionPersonaSettingsView Settings,
    ProactiveCompanionPromptView? CurrentPrompt,
    int TotalResponses,
    int TotalIgnores,
    int ConsecutiveIgnores,
    int TodayPromptCount,
    DateOnly LocalDate)
{
    public static CompanionPersonaView Default { get; } = new(
        CompanionPersonaSettingsView.Default,
        null,
        0,
        0,
        0,
        0,
        DateOnly.MinValue);
}

public sealed record NaturalLanguageOperationCandidate(
    Guid CandidateId,
    CandidateOperationKind Kind,
    string OriginalText,
    CommitmentDraft? Commitment = null,
    CommitmentRevisionDraft? Revision = null,
    string Summary = "",
    TemplateCommitmentDraft? FromTemplate = null,
    RecurrenceDraft? Recurrence = null,
    CandidateSource Source = CandidateSource.Desktop,
    DateTimeOffset? CreatedAt = null,
    CommitmentTemplateDraft? Template = null,
    Guid? TargetCommitmentId = null,
    int? ExpectedVersion = null,
    DateTimeOffset? DeferredStartAt = null,
    string? Reason = null);

public sealed record CompanionSnapshot(
    WorktimeChannelView WorktimeChannel,
    IReadOnlyList<MobileEscalationCard> MobileCards,
    IReadOnlyList<CommitmentReviewView> CommitmentReviews,
    DailyReviewView DailyReview,
    CycleReviewView CycleReview,
    AiStatusView Ai,
    IReadOnlyList<AiRequestRecordView> RecentAiRequests,
    IReadOnlyList<ChatMessageView> RecentChat,
    NaturalLanguageOperationCandidate? PendingCandidate,
    AiReviewDraftView? PendingAiReviewDraft = null,
    IReadOnlyList<AiReviewDraftView>? AiReviewDraftHistory = null,
    AiTrialEvidenceView? TrialEvidence = null,
    CompanionPersonaView? Persona = null,
    DataGovernanceStatusView? DataGovernance = null,
    BackupStatusView? Backup = null)
{
    [JsonIgnore]
    public IReadOnlyList<AiReviewDraftView> ConfirmedAiReviewDrafts => AiReviewDraftHistory ?? [];

    [JsonIgnore]
    public AiTrialEvidenceView AiTrialEvidence => TrialEvidence ?? AiTrialEvidenceView.Empty;

    [JsonIgnore]
    public CompanionPersonaView PersonaProjection => Persona ?? CompanionPersonaView.Default;

    [JsonIgnore]
    public DataGovernanceStatusView DataGovernanceProjection =>
        DataGovernance ?? DataGovernanceStatusView.Default;

    [JsonIgnore]
    public BackupStatusView BackupProjection => Backup ?? BackupStatusView.NotConfigured;

    public static CompanionSnapshot Empty { get; } = new(
        new(false, false, false, null, null, null),
        [],
        [],
        new(ReviewSessionState.NotDue, new TimeOnly(23, 0)),
        new(ReviewSessionState.NotDue, 14),
        new(false, "SiliconFlow",
            "DeepSeek-V4-Flash（全局）",
            null, 0m, 30m, false, false, null),
        [],
        [],
        null);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ConfigureWorktimeChannelCommand), "configureWorktime")]
[JsonDerivedType(typeof(BindWorktimeUserCommand), "bindWorktimeUser")]
[JsonDerivedType(typeof(HandleWorktimeActionCommand), "handleWorktimeAction")]
[JsonDerivedType(typeof(HandleWorktimeTextCommand), "handleWorktimeText")]
[JsonDerivedType(typeof(EndCommitmentEarlyCommand), "endCommitmentEarly")]
[JsonDerivedType(typeof(CancelCommitmentCommand), "cancelCommitment")]
[JsonDerivedType(typeof(DeferActiveCommitmentCommand), "deferActiveCommitment")]
[JsonDerivedType(typeof(SubmitCommitmentReviewCommand), "submitCommitmentReview")]
[JsonDerivedType(typeof(DeferCommitmentReviewCommand), "deferCommitmentReview")]
[JsonDerivedType(typeof(SkipCommitmentReviewCommand), "skipCommitmentReview")]
[JsonDerivedType(typeof(ConfigureDailyReviewCommand), "configureDailyReview")]
[JsonDerivedType(typeof(StartDailyReviewCommand), "startDailyReview")]
[JsonDerivedType(typeof(RespondDailyReviewCommand), "respondDailyReview")]
[JsonDerivedType(typeof(SnoozeDailyReviewCommand), "snoozeDailyReview")]
[JsonDerivedType(typeof(SkipDailyReviewCommand), "skipDailyReview")]
[JsonDerivedType(typeof(ConfigureCycleReviewCommand), "configureCycleReview")]
[JsonDerivedType(typeof(StartCycleReviewCommand), "startCycleReview")]
[JsonDerivedType(typeof(ConfirmCycleFocusesCommand), "confirmCycleFocuses")]
[JsonDerivedType(typeof(SaveAiCredentialCommand), "saveAiCredential")]
[JsonDerivedType(typeof(DeleteAiCredentialCommand), "deleteAiCredential")]
[JsonDerivedType(typeof(SetAiMonthlyHardCapCommand), "setAiMonthlyHardCap")]
[JsonDerivedType(typeof(SetAiModelPreferenceCommand), "setAiModelPreference")]
[JsonDerivedType(typeof(RequestAiChatCommand), "requestAiChat")]
[JsonDerivedType(typeof(InterpretNaturalLanguageCommand), "interpretNaturalLanguage")]
[JsonDerivedType(typeof(ConfirmNaturalLanguageCandidateCommand), "confirmNaturalLanguageCandidate")]
[JsonDerivedType(typeof(DiscardNaturalLanguageCandidateCommand), "discardNaturalLanguageCandidate")]
[JsonDerivedType(typeof(GenerateAiReviewDraftCommand), "generateAiReviewDraft")]
[JsonDerivedType(typeof(ConfirmAiReviewDraftCommand), "confirmAiReviewDraft")]
[JsonDerivedType(typeof(DiscardAiReviewDraftCommand), "discardAiReviewDraft")]
[JsonDerivedType(typeof(RecordManualAiComparisonCommand), "recordManualAiComparison")]
[JsonDerivedType(typeof(ConfigureCompanionPersonaCommand), "configureCompanionPersona")]
[JsonDerivedType(typeof(AcknowledgeProactiveCompanionCommand), "acknowledgeProactiveCompanion")]
[JsonDerivedType(typeof(RespondProactiveCompanionCommand), "respondProactiveCompanion")]
[JsonDerivedType(typeof(DismissProactiveCompanionCommand), "dismissProactiveCompanion")]
[JsonDerivedType(typeof(SetDetailedTimelineRetentionCommand), "setDetailedTimelineRetention")]
[JsonDerivedType(typeof(QueryDataRangeCommand), "queryDataRange")]
[JsonDerivedType(typeof(ExportDataRangeCommand), "exportDataRange")]
[JsonDerivedType(typeof(PreparePermanentDataDeletionCommand), "preparePermanentDataDeletion")]
[JsonDerivedType(typeof(ConfirmPermanentDataDeletionCommand), "confirmPermanentDataDeletion")]
[JsonDerivedType(typeof(ConfigureBackupCommand), "configureBackup")]
[JsonDerivedType(typeof(ForgetBackupPasswordCommand), "forgetBackupPassword")]
[JsonDerivedType(typeof(CreateBackupCommand), "createBackup")]
[JsonDerivedType(typeof(TestBackupRestoreCommand), "testBackupRestore")]
[JsonDerivedType(typeof(ScheduleBackupRestoreCommand), "scheduleBackupRestore")]
public abstract record CompanionCommand;

public sealed record ConfigureWorktimeChannelCommand(
    bool Enabled,
    string CliPath,
    string Profile,
    NotificationPreviewMode PreviewMode = NotificationPreviewMode.Privacy) : CompanionCommand;

public sealed record BindWorktimeUserCommand(
    string EventId,
    string SenderId,
    string ChatId,
    string MessageId) : CompanionCommand;

public sealed record HandleWorktimeActionCommand(
    string EventId,
    string SenderId,
    Guid CardId,
    Guid CommitmentId,
    int ExpectedVersion,
    WorktimeActionKind Action,
    DateTimeOffset? RestEndAt,
    int? RestMinutes = null) : CompanionCommand;

public sealed record HandleWorktimeTextCommand(
    string EventId,
    string SenderId,
    string ChatId,
    string MessageId,
    string Text,
    DateTimeOffset ReceivedAt) : CompanionCommand;

public sealed record EndCommitmentEarlyCommand(Guid CommitmentId, int ExpectedVersion) : CompanionCommand;

public sealed record CancelCommitmentCommand(
    Guid CommitmentId,
    int ExpectedVersion,
    string Reason) : CompanionCommand;

public sealed record DeferActiveCommitmentCommand(
    Guid CommitmentId,
    int ExpectedVersion,
    DateTimeOffset NewStartAt,
    string Reason) : CompanionCommand;

public sealed record SubmitCommitmentReviewCommand(
    Guid CommitmentId,
    string RawText,
    CompletionAssessment? Assessment) : CompanionCommand;

public sealed record DeferCommitmentReviewCommand(Guid CommitmentId, int Minutes) : CompanionCommand;

public sealed record SkipCommitmentReviewCommand(Guid CommitmentId) : CompanionCommand;

public sealed record ConfigureDailyReviewCommand(TimeOnly LocalTime) : CompanionCommand;

public sealed record StartDailyReviewCommand : CompanionCommand;

public sealed record RespondDailyReviewCommand(Guid SessionId, string RawText) : CompanionCommand;

public sealed record SnoozeDailyReviewCommand(int Minutes) : CompanionCommand;

public sealed record SkipDailyReviewCommand : CompanionCommand;

public sealed record ConfigureCycleReviewCommand(
    DateOnly AnchorDate,
    int IntervalDays,
    TimeOnly LocalTime) : CompanionCommand;

public sealed record StartCycleReviewCommand : CompanionCommand;

public sealed record ConfirmCycleFocusesCommand(IReadOnlyList<string> Focuses) : CompanionCommand;

public sealed record SaveAiCredentialCommand(string Credential) : CompanionCommand;

public sealed record DeleteAiCredentialCommand : CompanionCommand;

public sealed record SetAiMonthlyHardCapCommand(decimal HardCapCny) : CompanionCommand;

public sealed record SetAiModelPreferenceCommand(AiModelPreference Preference) : CompanionCommand;

public sealed record RequestAiChatCommand(
    string Text,
    bool ApprovedEstimatedCostOverOneCny = false,
    int MaxOutputTokens = 2048) : CompanionCommand;

public sealed record ConfigureCompanionPersonaCommand(
    CompanionPersonaSettingsView Settings) : CompanionCommand;

public sealed record AcknowledgeProactiveCompanionCommand(Guid PromptId) : CompanionCommand;

public sealed record RespondProactiveCompanionCommand(
    Guid PromptId,
    string ResponseText) : CompanionCommand;

public sealed record DismissProactiveCompanionCommand(Guid PromptId) : CompanionCommand;

public sealed record InterpretNaturalLanguageCommand(
    string Text,
    CandidateSource Source,
    string? SourceEventId = null) : CompanionCommand;

public sealed record ConfirmNaturalLanguageCandidateCommand(Guid CandidateId) : CompanionCommand;

public sealed record DiscardNaturalLanguageCandidateCommand(Guid CandidateId) : CompanionCommand;

public sealed record GenerateAiReviewDraftCommand(
    AiReviewKind Kind,
    bool ApprovedEstimatedCostOverOneCny = false) : CompanionCommand;

public sealed record ConfirmAiReviewDraftCommand(
    Guid DraftId,
    string ConfirmedText,
    int QualityRating,
    bool StructureReliable,
    bool AmbiguityHandled,
    bool NoOverreach,
    bool PrivacyScopeConfirmed,
    string? Note = null) : CompanionCommand;

public sealed record DiscardAiReviewDraftCommand(Guid DraftId) : CompanionCommand;

public sealed record RecordManualAiComparisonCommand(
    Guid DraftId,
    string Model,
    string OutputText,
    int QualityRating,
    bool StructureReliable,
    bool AmbiguityHandled,
    bool NoOverreach,
    bool PrivacyScopeConfirmed,
    string? Note = null) : CompanionCommand;

public sealed record CompanionOutcome(
    bool Success,
    string? ErrorCode = null,
    string? Message = null,
    CompanionSnapshot? Snapshot = null,
    NaturalLanguageOperationCandidate? Candidate = null,
    string? AssistantText = null,
    IReadOnlyList<string>? MissingInformation = null,
    DataRangeView? DataRange = null,
    DataDeletionCard? DataDeletion = null,
    BackupOperationView? BackupOperation = null);
