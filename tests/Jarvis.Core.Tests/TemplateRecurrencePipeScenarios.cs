using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class TemplateRecurrencePipeScenarios
{
    [Fact]
    public async Task Successful_mutation_reports_refresh_warning_when_projection_is_temporarily_unavailable()
    {
        using var database = new TemporaryDatabase();
        var now = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        await using var module = await SupervisionModule.OpenAsync(
            database.Path,
            new FakeClock(now),
            new FakeActivitySource(),
            new FakeReminderSink());
        var handler = new CoreCommandHandler(
            module,
            _ => Task.FromException<SupervisionSnapshot>(new IOException("projection unavailable")));

        var response = await handler.HandleAsync(new CoreRequest(
            CoreOperations.CreateTemplate,
            TemplateDraft: OfflineTemplate("仍应保存", 60)), CancellationToken.None);

        Assert.True(response.Success);
        Assert.Null(response.Snapshot);
        Assert.Contains("正式写入已成功", response.Message, StringComparison.Ordinal);
        Assert.Single((await module.GetSnapshotAsync()).Templates);
    }

    [Fact]
    public async Task Template_and_recurrence_commands_round_trip_through_the_core_pipe()
    {
        using var database = new TemporaryDatabase();
        var now = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        await using var module = await SupervisionModule.OpenAsync(
            database.Path,
            new FakeClock(now),
            new FakeActivitySource(),
            new FakeReminderSink());
        var pipeName = $"Jarvis.Core.Tests.{Guid.NewGuid():N}";
        await using var server = new CorePipeServer(pipeName, new CoreCommandHandler(module));
        server.Start();

        var created = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.CreateTemplate,
            TemplateDraft: OfflineTemplate("复盘模板", 60)));

        Assert.True(created.Success);
        Assert.NotNull(created.Template);
        Assert.Empty(created.Snapshot!.Commitments);
        var templateId = created.Template.Id;

        var fromTemplate = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.PrepareFromTemplate,
            TemplateCommitmentDraft: new TemplateCommitmentDraft(
                templateId,
                now.AddDays(1),
                DurationMinutes: 45)));
        Assert.True(fromTemplate.Success);
        Assert.Equal(templateId, fromTemplate.Card!.TemplateId);

        var confirmedOnce = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.Confirm,
            CandidateId: fromTemplate.Card.CandidateId));
        Assert.True(confirmedOnce.Success);
        Assert.Single(confirmedOnce.Snapshot!.Commitments);

        var updated = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.UpdateTemplate,
            TemplateId: templateId,
            TemplateDraft: OfflineTemplate("复盘模板（新版）", 90)));
        Assert.True(updated.Success);
        Assert.Equal(90, updated.Template!.DurationMinutes);
        Assert.Equal(45, (updated.Snapshot!.Commitments.Single().EndAt -
                          updated.Snapshot.Commitments.Single().StartAt).TotalMinutes);

        var recurrence = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.PrepareRecurrence,
            RecurrenceDraft: new RecurrenceDraft(
                OfflineDraft(now.AddDays(2), 30, templateId),
                new RecurrencePattern(
                    RecurrenceKind.SelectedDates,
                    SelectedDates:
                    [
                        new DateOnly(2026, 8, 14),
                        new DateOnly(2026, 8, 15),
                        new DateOnly(2026, 8, 16)
                    ]))));
        Assert.True(recurrence.Success);
        Assert.Equal(3, recurrence.RecurrenceCard!.Occurrences.Count);

        var confirmedPlan = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.ConfirmRecurrence,
            CandidateId: recurrence.RecurrenceCard.CandidateId));
        Assert.True(confirmedPlan.Success);
        Assert.Equal(3, confirmedPlan.RecurrencePlan!.Occurrences.Count);

        var anchor = confirmedPlan.RecurrencePlan.Occurrences[1];
        var changePreview = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.PrepareRecurrenceChange,
            RecurrenceChange: new RecurrenceChangeRequest(
                confirmedPlan.RecurrencePlan.Id,
                anchor.CommitmentId,
                RecurrenceChangeKind.Skip,
                RecurrenceChangeScope.ThisAndFuture)));
        Assert.True(changePreview.Success);
        Assert.Equal(2, changePreview.RecurrenceChangeCard!.AffectedOccurrences.Count);
        var changed = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.ConfirmRecurrenceChange,
            CandidateId: changePreview.RecurrenceChangeCard.CandidateId));
        Assert.True(changed.Success);
        Assert.Equal(2, changed.RecurrencePlan!.Occurrences.Count(occurrence =>
            occurrence.Status == RecurrenceOccurrenceStatus.Skipped));

        var archived = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.ArchiveTemplate,
            TemplateId: templateId));
        Assert.True(archived.Success);
        Assert.True(archived.Template!.IsArchived);
        Assert.Equal(4, archived.Snapshot!.Commitments.Count);
    }

    private static CommitmentTemplateDraft OfflineTemplate(string name, int minutes) => new(
        name,
        CommitmentKind.Offline,
        minutes,
        InputGoal: "口语复盘",
        OutcomeGoal: "留下当天总结",
        RelatedAppsOrSites: null,
        SupervisionMode: null,
        ReminderSettings: null,
        ActivityRules: null,
        RestSettings: new RestSettings(10, 15));

    private static CommitmentDraft OfflineDraft(
        DateTimeOffset startAt,
        int durationMinutes,
        Guid templateId) => new(
        CommitmentKind.Offline,
        startAt,
        EndAt: null,
        durationMinutes,
        InputGoal: "口语复盘",
        OutcomeGoal: null,
        RelatedAppsOrSites: null,
        SupervisionMode: null,
        ReminderSettings: null,
        RestSettings: new RestSettings(10, 15),
        TemplateId: templateId);

    private static async Task<CoreResponse> SendAsync(string pipeName, CoreRequest request)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, CoreProtocol.Json));
        var response = await reader.ReadLineAsync();
        return JsonSerializer.Deserialize<CoreResponse>(response!, CoreProtocol.Json)!;
    }
}
