using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed record StoredMobilePairingOffer(string SecretHash, DateTimeOffset ExpiresAt);

internal sealed record StoredMobilePairing(
    string DeviceId,
    string DeviceName,
    string TokenHash,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt = null);

internal interface IMobileSyncStore
{
    Task<StoredMobilePairingOffer?> ReadOfferAsync(CancellationToken cancellationToken);
    Task SaveOfferAsync(StoredMobilePairingOffer offer, CancellationToken cancellationToken);
    Task ClearOfferAsync(CancellationToken cancellationToken);
    Task<StoredMobilePairing?> ReadPairingAsync(CancellationToken cancellationToken);
    Task SavePairingAsync(StoredMobilePairing pairing, CancellationToken cancellationToken);
    Task SaveHealthAsync(MobileHealthReport health, DateTimeOffset receivedAt, CancellationToken cancellationToken);
    Task<(MobileHealthReport Health, DateTimeOffset ReceivedAt)?> ReadHealthAsync(CancellationToken cancellationToken);
    Task<bool> TryAppendEventAsync(MobileExecutionEvent value, CancellationToken cancellationToken);
}

internal sealed class MobileSyncModule(
    IMobileSyncStore store,
    IClock clock,
    string endpoint,
    string certificateFingerprint)
{
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(30);
    private string? _transportError;

    public void ReportTransportFailure(Exception exception) =>
        Volatile.Write(ref _transportError, exception.Message);

    public async Task<MobilePairingOffer> CreatePairingOfferAsync(
        CancellationToken cancellationToken = default)
    {
        var existing = await store.ReadPairingAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { RevokedAt: null })
            throw new InvalidOperationException("已有手机与 Jarvis 配对；请先撤销旧手机。 ");

        var secret = CreateSecret(24);
        var expiresAt = clock.Now.Add(PairingLifetime);
        await store.SaveOfferAsync(
            new StoredMobilePairingOffer(Hash(secret), expiresAt), cancellationToken).ConfigureAwait(false);
        var payload = BuildQrPayload(endpoint, certificateFingerprint, secret, expiresAt);
        return new MobilePairingOffer(
            MobileProtocol.Version, endpoint, certificateFingerprint, secret, expiresAt, payload);
    }

    public async Task<MobilePairResponse> PairAsync(
        MobilePairRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion != MobileProtocol.Version)
            throw new MobileProtocolException("protocol_version", "手机端协议版本不受支持。");
        if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.DeviceName))
            throw new MobileProtocolException("invalid_device", "手机身份不完整。");

        var existing = await store.ReadPairingAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { RevokedAt: null })
            throw new MobileProtocolException("already_paired", "已有手机与 Jarvis 配对。");
        var offer = await store.ReadOfferAsync(cancellationToken).ConfigureAwait(false);
        if (offer is null || offer.ExpiresAt <= clock.Now ||
            !FixedEquals(offer.SecretHash, Hash(request.OneTimeSecret)))
            throw new MobileProtocolException("pairing_rejected", "配对码无效或已过期。");

        var token = CreateSecret(32);
        var pairedAt = clock.Now;
        await store.SavePairingAsync(
            new StoredMobilePairing(
                request.DeviceId.Trim(), request.DeviceName.Trim(), Hash(token), pairedAt),
            cancellationToken).ConfigureAwait(false);
        await store.ClearOfferAsync(cancellationToken).ConfigureAwait(false);
        return new MobilePairResponse(MobileProtocol.Version, token, pairedAt);
    }

    public async Task<MobileSyncResponse> SynchronizeAsync(
        string token,
        MobileSyncRequest request,
        SupervisionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion != MobileProtocol.Version)
            throw new MobileProtocolException("protocol_version", "手机端协议版本不受支持。");
        var pairing = await RequirePairingAsync(token, request.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (request.Health.DeviceId != pairing.DeviceId)
            throw new MobileProtocolException("device_mismatch", "状态上报来自另一台手机。");

        await store.SaveHealthAsync(request.Health, clock.Now, cancellationToken).ConfigureAwait(false);
        var accepted = new List<Guid>(request.PendingEvents.Count);
        foreach (var value in request.PendingEvents)
        {
            if (await store.TryAppendEventAsync(value, cancellationToken).ConfigureAwait(false))
                accepted.Add(value.EventId);
            else
                accepted.Add(value.EventId); // Duplicate means it was accepted by an earlier retry.
        }

        var policy = MobilePolicyMapper.Select(snapshot, clock.Now);
        var revoked = request.Health.ActiveCommitmentId is { } active &&
                      active != policy?.CommitmentId
            ? new[] { active }
            : [];
        return new MobileSyncResponse(
            MobileProtocol.Version,
            clock.Now,
            new MobilePolicyDirective(Generation(policy), policy, revoked),
            accepted);
    }

    public async Task<MobileConnectionProjection> GetProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        var pairing = await store.ReadPairingAsync(cancellationToken).ConfigureAwait(false);
        if (pairing is null)
            return Empty(MobileConnectionState.Unpaired, "尚未配对手机。");
        if (pairing.RevokedAt is not null)
            return Empty(MobileConnectionState.Revoked, "手机配对已撤销。");

        var stored = await store.ReadHealthAsync(cancellationToken).ConfigureAwait(false);
        var transportError = Volatile.Read(ref _transportError);
        if (stored is null)
            return new MobileConnectionProjection(
                transportError is null ? MobileConnectionState.Offline : MobileConnectionState.Degraded,
                pairing.DeviceName, pairing.PairedAt, null,
                null, null, null, null, null, null, null, "等待手机首次同步。");

        var (health, receivedAt) = stored.Value;
        var requiredReady = health.UsageAccess && health.Overlay;
        var state = transportError is not null
            ? MobileConnectionState.Degraded
            : clock.Now - receivedAt > OfflineAfter
            ? MobileConnectionState.Offline
            : requiredReady && health.State == MobileConnectionState.Ready
                ? MobileConnectionState.Ready
                : MobileConnectionState.Degraded;
        return new MobileConnectionProjection(
            state, pairing.DeviceName, pairing.PairedAt, receivedAt,
            health.UsageAccess, health.Overlay, health.Notifications, health.ExactAlarm,
            health.BackgroundAllowed, health.ActiveCommitmentId, health.ActivePolicyVersion,
            transportError is not null
                ? $"电脑局域网同步服务不可用：{transportError}"
                : state == MobileConnectionState.Offline
                    ? "手机暂时离线，将继续执行本地缓存策略。"
                    : health.Detail);
    }

    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        var pairing = await store.ReadPairingAsync(cancellationToken).ConfigureAwait(false);
        if (pairing is not null && pairing.RevokedAt is null)
            await store.SavePairingAsync(pairing with { RevokedAt = clock.Now }, cancellationToken)
                .ConfigureAwait(false);
        await store.ClearOfferAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredMobilePairing> RequirePairingAsync(
        string token,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var pairing = await store.ReadPairingAsync(cancellationToken).ConfigureAwait(false);
        if (pairing is null || pairing.RevokedAt is not null ||
            !string.Equals(pairing.DeviceId, deviceId, StringComparison.Ordinal) ||
            !FixedEquals(pairing.TokenHash, Hash(token)))
            throw new MobileProtocolException("unauthorized", "手机身份验证失败。");
        return pairing;
    }

    private static MobileConnectionProjection Empty(MobileConnectionState state, string detail) =>
        new(state, null, null, null, null, null, null, null, null, null, null, detail);

    private static string BuildQrPayload(
        string endpoint,
        string fingerprint,
        string secret,
        DateTimeOffset expiresAt) =>
        $"jarvis://pair?v={MobileProtocol.Version}" +
        $"&endpoint={Uri.EscapeDataString(endpoint)}" +
        $"&fingerprint={Uri.EscapeDataString(fingerprint)}" +
        $"&secret={Uri.EscapeDataString(secret)}" +
        $"&expires={Uri.EscapeDataString(expiresAt.ToString("O"))}";

    private static long Generation(MobileBlockPolicy? policy)
    {
        if (policy is null) return 0;
        var material = Encoding.UTF8.GetBytes(
            $"{policy.CommitmentId:D}|{policy.Version}|{policy.StartAt:O}|{policy.EndAt:O}");
        return BitConverter.ToInt64(SHA256.HashData(material), 0) & long.MaxValue;
    }

    private static string CreateSecret(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));
}

internal static class MobilePolicyMapper
{
    public static MobileBlockPolicy? Select(SupervisionSnapshot snapshot, DateTimeOffset now)
    {
        var commitment = snapshot.Commitments
            .Where(value => value.Kind == CommitmentKind.Computer &&
                            value.EndAt > now &&
                            value.Phase is not (CommitmentPhase.AwaitingReview or CommitmentPhase.Skipped))
            .OrderBy(value => value.StartAt <= now ? 0 : 1)
            .ThenBy(value => value.StartAt)
            .FirstOrDefault();
        return commitment is null
            ? null
            : new MobileBlockPolicy(
                commitment.Id,
                commitment.Version,
                commitment.StartAt,
                commitment.EndAt,
                commitment.OutcomeGoal ?? commitment.InputGoal ?? "专注工作",
                MobileTargetPackages.Defaults);
    }
}

internal sealed class MobileProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
