using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Contracts;

public static class MobileProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 42731;
    public static readonly JsonSerializerOptions Json = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class MobileTargetPackages
{
    public const string Douyin = "com.ss.android.ugc.aweme";
    public const string Bilibili = "tv.danmaku.bili";
    public const string Xiaohongshu = "com.xingin.xhs";
    public const string Wechat = "com.tencent.mm";

    public static readonly IReadOnlyList<string> Defaults =
        [Douyin, Bilibili, Xiaohongshu, Wechat];
}

public enum MobileConnectionState
{
    Unpaired,
    Ready,
    Degraded,
    Offline,
    Revoked
}

public enum MobileEventKind
{
    PolicyAccepted,
    PolicyActivated,
    AppBlocked,
    TemporaryAccessStarted,
    TemporaryAccessEnded,
    PolicyExpired,
    AvailabilityChanged,
    QuickRecord
}

public sealed record MobileBlockPolicy(
    Guid CommitmentId,
    int Version,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string DisplayTitle,
    IReadOnlyList<string> BlockedPackages);

public sealed record MobileExecutionEvent(
    Guid EventId,
    MobileEventKind Kind,
    DateTimeOffset OccurredAt,
    Guid? CommitmentId = null,
    int? PolicyVersion = null,
    string? PackageName = null,
    string? Reason = null,
    string? Detail = null);

public sealed record MobileHealthReport(
    string DeviceId,
    DateTimeOffset ObservedAt,
    MobileConnectionState State,
    bool UsageAccess,
    bool Overlay,
    bool Notifications,
    bool ExactAlarm,
    bool BackgroundAllowed,
    Guid? ActiveCommitmentId = null,
    int? ActivePolicyVersion = null,
    string? Detail = null);

public sealed record MobileSyncRequest(
    int ProtocolVersion,
    string DeviceId,
    MobileHealthReport Health,
    IReadOnlyList<MobileExecutionEvent> PendingEvents);

public sealed record MobilePolicyDirective(
    long Generation,
    MobileBlockPolicy? Policy,
    IReadOnlyList<Guid> RevokedCommitmentIds);

public sealed record MobileSyncResponse(
    int ProtocolVersion,
    DateTimeOffset ServerTime,
    MobilePolicyDirective Directive,
    IReadOnlyList<Guid> AcceptedEventIds,
    string? LatestMessage = null);

public sealed record MobilePairingOffer(
    int ProtocolVersion,
    string Endpoint,
    string CertificateFingerprint,
    string OneTimeSecret,
    DateTimeOffset ExpiresAt,
    string QrPayload);

public sealed record MobilePairRequest(
    int ProtocolVersion,
    string DeviceId,
    string DeviceName,
    string OneTimeSecret);

public sealed record MobilePairResponse(
    int ProtocolVersion,
    string DeviceToken,
    DateTimeOffset PairedAt);

public sealed record MobileConnectionProjection(
    MobileConnectionState State,
    string? DeviceName,
    DateTimeOffset? PairedAt,
    DateTimeOffset? LastSeenAt,
    bool? UsageAccess,
    bool? Overlay,
    bool? Notifications,
    bool? ExactAlarm,
    bool? BackgroundAllowed,
    Guid? ActiveCommitmentId,
    int? ActivePolicyVersion,
    string? Detail);
