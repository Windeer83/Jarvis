using System.Text;
using System.Security.Cryptography;
using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class DataGovernanceScenarios
{
    [Fact]
    public async Task Expired_timeline_becomes_daily_summary_while_commitment_and_review_are_retained()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 1, 4, 0, 0, TimeSpan.Zero));
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var commitment = await CreateCommitmentAsync(supervision, clock);
        activity.Next = new ActivityObservation(
            ActivityAvailability.Available, true, "work", clock.Now, IdleDuration: TimeSpan.Zero);
        await supervision.TickAsync();
        clock.Now = clock.Now.AddMinutes(30);
        activity.Next = activity.Next with { ObservedAt = clock.Now };
        await supervision.TickAsync();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), new FakeCredentialStore());
        Assert.True((await companion.DispatchAsync(new SetDetailedTimelineRetentionCommand(30))).Success);

        clock.Now = new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero);
        await companion.AdvanceAsync();
        var queried = await companion.DispatchAsync(new QueryDataRangeCommand(
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1)));

        Assert.True(queried.Success, queried.Message);
        var range = Assert.IsType<DataRangeView>(queried.DataRange);
        Assert.Empty(range.Timeline);
        var summary = Assert.Single(range.DailySummaries);
        Assert.InRange(summary.ObservedSeconds, 1799, 1801);
        Assert.InRange(summary.RelatedSeconds, 1799, 1801);
        Assert.Equal(commitment.Id, Assert.Single(range.Commitments).CommitmentId);
        Assert.Equal(30, queried.Snapshot!.DataGovernanceProjection.DetailedTimelineRetentionDays);
    }

    [Fact]
    public async Task Password_export_excludes_credentials_and_chat_and_permanent_delete_requires_exact_phrase()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 1, 4, 0, 0, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        await CreateCommitmentAsync(supervision, clock);
        var credentials = new FakeCredentialStore();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), credentials);
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-export-secret-never-include"));
        await companion.DispatchAsync(new RequestAiChatCommand("SECRET_CHAT_MUST_NOT_EXPORT"));

        var directory = Path.Combine(Path.GetTempPath(), "jarvis-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "range.jarvis-export");
            const string password = "correct horse battery staple";
            var exported = await companion.DispatchAsync(new ExportDataRangeCommand(
                new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1), path, password));
            Assert.True(exported.Success, exported.Message);
            var plaintext = Encoding.UTF8.GetString(EncryptedDataExport.Decrypt(
                await File.ReadAllBytesAsync(path), password));
            Assert.Contains("Jarvis supervision export", plaintext, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-export-secret", plaintext, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET_CHAT_MUST_NOT_EXPORT", plaintext, StringComparison.Ordinal);
            Assert.ThrowsAny<CryptographicException>(() =>
                EncryptedDataExport.Decrypt(File.ReadAllBytes(path), "wrong password 123"));

            clock.Now = new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero);
            var preview = await companion.DispatchAsync(new PreparePermanentDataDeletionCommand(
                new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1),
                DataDeletionScope.AllSupervisionRecords));
            var card = Assert.IsType<DataDeletionCard>(preview.DataDeletion);
            var rejected = await companion.DispatchAsync(new ConfirmPermanentDataDeletionCommand(
                card.CandidateId, "删除"));
            Assert.False(rejected.Success);
            var deleted = await companion.DispatchAsync(new ConfirmPermanentDataDeletionCommand(
                card.CandidateId, card.ConfirmationPhrase));
            Assert.True(deleted.Success, deleted.Message);
            var after = await companion.DispatchAsync(new QueryDataRangeCommand(
                card.StartDate, card.EndDate));
            Assert.Empty(after.DataRange!.Commitments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Retention_and_ranges_fail_closed_outside_documented_bounds()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), new FakeCredentialStore());

        Assert.Equal("retention_days_invalid",
            (await companion.DispatchAsync(new SetDetailedTimelineRetentionCommand(6))).ErrorCode);
        Assert.Equal("data_range_invalid",
            (await companion.DispatchAsync(new QueryDataRangeCommand(
                new DateOnly(2026, 8, 16), new DateOnly(2026, 8, 15)))).ErrorCode);
        Assert.Equal("data_deletion_invalid",
            (await companion.DispatchAsync(new PreparePermanentDataDeletionCommand(
                new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 15),
                DataDeletionScope.AllSupervisionRecords))).ErrorCode);
        Assert.Equal("data_export_failed",
            (await companion.DispatchAsync(new ExportDataRangeCommand(
                new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 15), database.Path,
                "correct horse battery staple"))).ErrorCode);
        Assert.True((await companion.DispatchAsync(new QueryDataRangeCommand(
            new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 15)))).Success);
    }

    [Fact]
    public async Task Retention_splits_a_cross_midnight_segment_into_the_correct_local_daily_summaries()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 1, 23, 50, 0, TimeSpan.FromHours(8)));
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var prepared = await supervision.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Computer, clock.Now, null, 40, "跨日汇总", null,
            [new CommitmentTarget(CommitmentTargetKind.Application, "work.exe")],
            SupervisionMode.Interactive, null));
        Assert.True(prepared.Success, prepared.Message);
        Assert.True((await supervision.ConfirmAsync(prepared.Value!.CandidateId)).Success);
        activity.Next = new ActivityObservation(
            ActivityAvailability.Available, true, "work", clock.Now, IdleDuration: TimeSpan.Zero);
        await supervision.TickAsync();
        clock.Now = clock.Now.AddMinutes(30);
        activity.Next = activity.Next with { ObservedAt = clock.Now };
        await supervision.TickAsync();

        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), new FakeCredentialStore());
        Assert.True((await companion.DispatchAsync(new SetDetailedTimelineRetentionCommand(30))).Success);
        clock.Now = clock.Now.AddDays(100);
        await companion.AdvanceAsync();
        var range = (await companion.DispatchAsync(new QueryDataRangeCommand(
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2)))).DataRange!;

        Assert.Collection(range.DailySummaries,
            first =>
            {
                Assert.Equal(new DateOnly(2026, 4, 1), first.Date);
                Assert.InRange(first.ObservedSeconds, 599, 601);
            },
            second =>
            {
                Assert.Equal(new DateOnly(2026, 4, 2), second.Date);
                Assert.InRange(second.ObservedSeconds, 1199, 1201);
            });
    }

    private static async Task<CommitmentView> CreateCommitmentAsync(
        SupervisionModule supervision,
        FakeClock clock)
    {
        var prepared = await supervision.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Computer,
            clock.Now,
            null,
            60,
            "保留策略验证",
            null,
            [new CommitmentTarget(CommitmentTargetKind.Application, "work.exe")],
            SupervisionMode.Interactive,
            null));
        Assert.True(prepared.Success, prepared.Message);
        var confirmed = await supervision.ConfirmAsync(prepared.Value!.CandidateId);
        Assert.True(confirmed.Success, confirmed.Message);
        return confirmed.Value!;
    }
}
