using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class MobileSyncScenarios
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 14, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task OneTimePairingProducesAUsableTokenAndCannotBeReplayed()
    {
        using var database = new TemporaryDatabase();
        var (module, _, _) = await CreateAsync(database.Path);
        var offer = await module.CreatePairingOfferAsync();
        var request = new MobilePairRequest(1, "mate70", "HUAWEI Mate 70 Pro+", offer.OneTimeSecret);

        var pairing = await module.PairAsync(request);
        var response = await module.SynchronizeAsync(
            pairing.DeviceToken, SyncRequest(), EmptySnapshot());

        Assert.Equal(MobileProtocol.Version, response.ProtocolVersion);
        var replay = await Assert.ThrowsAsync<MobileProtocolException>(() => module.PairAsync(request));
        Assert.Equal("already_paired", replay.Code);
    }

    [Fact]
    public async Task InvalidTokenCannotCrossTheSyncSeam()
    {
        using var database = new TemporaryDatabase();
        var (module, _, _) = await CreateAsync(database.Path);
        var offer = await module.CreatePairingOfferAsync();
        await module.PairAsync(new MobilePairRequest(1, "mate70", "Mate 70", offer.OneTimeSecret));

        var error = await Assert.ThrowsAsync<MobileProtocolException>(() =>
            module.SynchronizeAsync("wrong-token", SyncRequest(), EmptySnapshot()));

        Assert.Equal("unauthorized", error.Code);
    }

    [Fact]
    public async Task ComputerCommitmentProjectsOnlyTheFourConfirmedPhoneTargets()
    {
        using var database = new TemporaryDatabase();
        var (module, _, _) = await CreateAsync(database.Path);
        var token = await PairAsync(module);
        var commitment = Commitment(Guid.NewGuid(), 3, Now.AddMinutes(-5), Now.AddMinutes(55));

        var response = await module.SynchronizeAsync(
            token, SyncRequest(), Snapshot(commitment));

        Assert.Equal(commitment.Id, response.Directive.Policy!.CommitmentId);
        Assert.Equal(3, response.Directive.Policy.Version);
        Assert.Equal(MobileTargetPackages.Defaults, response.Directive.Policy.BlockedPackages);
    }

    [Fact]
    public async Task RetryAcknowledgesDuplicateEventWithoutCreatingASecondRow()
    {
        using var database = new TemporaryDatabase();
        var (module, store, _) = await CreateAsync(database.Path);
        var token = await PairAsync(module);
        var executionEvent = new MobileExecutionEvent(
            Guid.NewGuid(), MobileEventKind.AppBlocked, Now, PackageName: MobileTargetPackages.Douyin);
        var request = SyncRequest([executionEvent]);

        var first = await module.SynchronizeAsync(token, request, EmptySnapshot());
        var retry = await module.SynchronizeAsync(token, request, EmptySnapshot());

        Assert.Equal([executionEvent.EventId], first.AcceptedEventIds);
        Assert.Equal([executionEvent.EventId], retry.AcceptedEventIds);
        Assert.False(await store.TryAppendEventAsync(executionEvent, CancellationToken.None));
    }

    [Fact]
    public async Task StalePhoneStatusIsExplicitlyOfflineAndKeepsCachedPolicySemantics()
    {
        using var database = new TemporaryDatabase();
        var (module, _, clock) = await CreateAsync(database.Path);
        var token = await PairAsync(module);
        await module.SynchronizeAsync(token, SyncRequest(), EmptySnapshot());
        clock.Now = Now.AddMinutes(1);

        var projection = await module.GetProjectionAsync();

        Assert.Equal(MobileConnectionState.Offline, projection.State);
        Assert.Contains("缓存策略", projection.Detail);
    }

    private static async Task<(MobileSyncModule Module, SqliteMobileSyncStore Store, FakeClock Clock)> CreateAsync(string path)
    {
        var store = new SqliteMobileSyncStore(path);
        await store.InitializeAsync();
        var clock = new FakeClock(Now);
        var module = new MobileSyncModule(store, clock, "https://192.168.1.2:42731", "AA11");
        return (module, store, clock);
    }

    private static async Task<string> PairAsync(MobileSyncModule module)
    {
        var offer = await module.CreatePairingOfferAsync();
        var response = await module.PairAsync(
            new MobilePairRequest(1, "mate70", "HUAWEI Mate 70 Pro+", offer.OneTimeSecret));
        return response.DeviceToken;
    }

    private static MobileSyncRequest SyncRequest(IReadOnlyList<MobileExecutionEvent>? events = null) =>
        new(1, "mate70", new MobileHealthReport(
            "mate70", Now, MobileConnectionState.Ready,
            UsageAccess: true, Overlay: true, Notifications: true,
            ExactAlarm: true, BackgroundAllowed: true), events ?? []);

    private static SupervisionSnapshot EmptySnapshot() =>
        new(Now, null, [], null, null);

    private static SupervisionSnapshot Snapshot(CommitmentView commitment) =>
        new(Now, commitment.Id, [commitment], null, null);

    private static CommitmentView Commitment(
        Guid id,
        int version,
        DateTimeOffset start,
        DateTimeOffset end) =>
        new(
            id, CommitmentKind.Computer, start, end,
            "打开 TradingView 和 Notion", "完成交易复盘", [],
            SupervisionMode.Passive,
            new ReminderSettings(true, 5, 5, 5, 3),
            CommitmentPhase.Supervising, Now.AddHours(-1), null, [],
            new RestSettings(5, 10), Version: version);
}
