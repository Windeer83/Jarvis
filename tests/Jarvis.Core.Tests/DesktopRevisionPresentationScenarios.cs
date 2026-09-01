using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Contracts;
using Jarvis.Desktop;
using Xunit;

namespace Jarvis.Core.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class DesktopRevisionPresentationScenarios
{
    [Fact]
    public void Revision_card_shows_only_meaningful_changes_reason_and_forward_effect()
    {
        var before = Card("写报告", "完成初稿", 60);
        var after = Card("写报告第二章", "完成初稿", 90);
        var summary = CommitmentRevisionSummary.Format(new CommitmentRevisionCard(
            Guid.NewGuid(), Guid.NewGuid(), 1, 2, DateTimeOffset.Parse("2026-08-12T08:30:00Z"),
            before, after, "临时增加第二章", "尚未写入。"));

        Assert.Contains("修改原因：临时增加第二章", summary);
        Assert.Contains("投入目标：写报告 → 写报告第二章", summary);
        Assert.Contains("时间：", summary);
        Assert.Contains("确认后立即向后生效", summary);
        Assert.DoesNotContain("成果目标：", summary);
    }

    [Fact]
    public void History_summary_keeps_versions_and_supervision_facts_visible()
    {
        var commitmentId = Guid.NewGuid();
        var at = DateTimeOffset.Parse("2026-08-12T08:30:00Z");
        var oldCard = new CommitmentCard(
            Guid.NewGuid(), CommitmentKind.Computer,
            DateTimeOffset.Parse("2026-08-12T01:02:03Z"),
            DateTimeOffset.Parse("2026-08-12T02:12:13Z"),
            "旧版投入目标", "旧版成果目标",
            [
                new CommitmentTarget(CommitmentTargetKind.Application, "OldWriter.exe"),
                new CommitmentTarget(CommitmentTargetKind.Website, "old.example.com")
            ],
            SupervisionMode.Passive,
            new ReminderSettings(false, 7, 17, 27, 4, SoundEnabled: false, QuietPresentation: true),
            "历史快照",
            [
                new ActivityRule(
                    new CommitmentTarget(CommitmentTargetKind.Application, "OldWriter.exe"),
                    ActivityClassification.Related),
                new ActivityRule(
                    new CommitmentTarget(CommitmentTargetKind.Website, "news.example.com"),
                    ActivityClassification.Distracting)
            ],
            new RestSettings(13, 23));
        var currentCard = Card("写报告第二章", "完成新稿", 90);
        var history = new CommitmentHistoryView(
            commitmentId,
            2,
            [
                new CommitmentRevisionVersionView(commitmentId, 1, at.AddHours(-1), at.AddHours(-1), "初始确认", oldCard),
                new CommitmentRevisionVersionView(commitmentId, 2, at, at, "增加第二章", currentCard)
            ],
            [new ActivitySegmentView(
                1, commitmentId, 1, at.AddMinutes(-5), at, ActivityAvailability.Available,
                new CommitmentTarget(CommitmentTargetKind.Application, "Word.exe"),
                ActivityClassification.Unknown, ActivityClassification.Related, false, null)],
            [new ReminderNotice(commitmentId, "请回到承诺", at, CommitmentVersion: 2)],
            [new ActivityCorrectionView(
                new CommitmentTarget(CommitmentTargetKind.Application, "Word.exe"),
                ActivityClassification.Unknown,
                ActivityClassification.Related,
                at.AddMinutes(-2),
                at,
                ActivityRuleScope.Template,
                "写报告的工具",
                CommitmentVersion: 2)],
            [new SupervisionResponseView(1, commitmentId, 1, "return_intent", at)]);

        var summary = CommitmentHistorySummary.Format(history);

        Assert.Contains("当前版本：v2", summary);
        Assert.Contains("增加第二章", summary);
        Assert.Contains("v1", summary);
        Assert.Contains(
            $"时间：{oldCard.StartAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} → {oldCard.EndAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}",
            summary);
        Assert.Contains("投入=旧版投入目标；成果=旧版成果目标", summary);
        Assert.Contains("Application:OldWriter.exe", summary);
        Assert.Contains("Website:old.example.com", summary);
        Assert.Contains("类型/模式：Computer/Passive", summary);
        Assert.Contains("开始=关；本地偏离=7分；首次移动=17分；移动重复=27分；最多=4；声音=关；安静=开", summary);
        Assert.Contains("Application:OldWriter.exe=Related", summary);
        Assert.Contains("Website:news.example.com=Distracting", summary);
        Assert.Contains("休息：空闲询问=13分；默认总时长=23分", summary);
        Assert.Contains("Word.exe", summary);
        Assert.Contains("请回到承诺", summary);
        Assert.Contains("v2", summary);
        Assert.Contains("Template", summary);
        Assert.Contains("写报告的工具", summary);
        Assert.Contains("return_intent", summary);
    }

    [Fact]
    public void Cancel_revision_discards_preview_card_and_disables_confirmation()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var before = Card("写报告", "完成初稿", 60);
                var after = Card("写报告第二章", "完成初稿", 90);
                var candidate = new CommitmentRevisionCard(
                    Guid.NewGuid(), Guid.NewGuid(), 1, 2,
                    DateTimeOffset.Parse("2026-08-12T08:30:00Z"),
                    before, after, "临时增加第二章", "尚未写入。");

                SetPrivateField(window, "_revisionCandidate", candidate);
                InvokePrivate(window, "ShowRevisionCard", candidate);

                var cardBorder = (Border)window.FindName("CardBorder");
                var cardText = (TextBlock)window.FindName("CardText");
                var confirmButton = (Button)window.FindName("ConfirmButton");
                Assert.Equal(Visibility.Visible, cardBorder.Visibility);
                Assert.NotEmpty(cardText.Text);

                InvokePrivate(window, "CancelRevisionButton_Click", window, new RoutedEventArgs());

                Assert.Null(GetPrivateField<CommitmentRevisionCard>(window, "_revisionCandidate"));
                Assert.Equal(Visibility.Collapsed, cardBorder.Visibility);
                Assert.Empty(cardText.Text);
                Assert.False(confirmButton.IsEnabled);

                InvokePrivate(window, "ShowRevisionCard", candidate);
                Assert.True(confirmButton.IsEnabled);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    [Fact]
    public void Candidate_card_transitions_replace_confirmation_label_instead_of_leaking_revision_state()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var before = Card("写报告", "完成初稿", 60);
                var after = Card("写报告第二章", "完成初稿", 90);
                var revision = new CommitmentRevisionCard(
                    Guid.NewGuid(), Guid.NewGuid(), 1, 2,
                    DateTimeOffset.Parse("2026-08-12T08:30:00Z"),
                    before, after, "临时增加第二章", "尚未写入。");
                var recurrence = new RecurrenceCard(
                    Guid.NewGuid(),
                    new RecurrencePattern(
                        RecurrenceKind.Daily,
                        new DateOnly(2026, 8, 12),
                        new DateOnly(2026, 8, 12)),
                    [before],
                    "尚未写入。");
                var start = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
                var recurrenceChange = new RecurrenceChangeCard(
                    Guid.NewGuid(), Guid.NewGuid(), RecurrenceChangeKind.Adjust,
                    RecurrenceChangeScope.ThisOccurrence,
                    [new RecurrenceChangeOccurrencePreview(
                        Guid.NewGuid(), new DateOnly(2026, 8, 14), start, start.AddHours(1),
                        RecurrenceOccurrenceStatus.Active, start.AddHours(1), start.AddHours(2),
                        RecurrenceOccurrenceStatus.Active, 1, 2)],
                    "尚未写入。", "会议冲突");
                var confirmButton = (Button)window.FindName("ConfirmButton");

                InvokePrivate(window, "ShowRevisionCard", revision);
                Assert.Equal("确认修订", confirmButton.Content);

                InvokePrivate(window, "ShowRecurrenceChangeCard", recurrenceChange);
                Assert.Equal("确认修改", confirmButton.Content);

                InvokePrivate(window, "ShowRecurrenceCard", recurrence);
                Assert.Equal("确认，正式成立", confirmButton.Content);

                InvokePrivate(window, "ShowRevisionCard", revision);
                InvokePrivate(window, "ShowCard", before);
                Assert.Equal("确认，正式成立", confirmButton.Content);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    [Fact]
    public void Revision_form_roundtrip_does_not_change_an_active_start_with_seconds()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var start = DateTimeOffset.Parse("2026-08-15T15:09:37+08:00");
                var target = new CommitmentTarget(CommitmentTargetKind.Application, "notion");
                var commitment = new CommitmentView(
                    Guid.NewGuid(), CommitmentKind.Computer, start, start.AddMinutes(25),
                    "交易的复盘", null, [target], SupervisionMode.Interactive,
                    new ReminderSettings(true, 2, 5, 5, 3),
                    CommitmentPhase.Supervising, start.AddMinutes(-2), null,
                    [new ActivityRule(target, ActivityClassification.Related)],
                    new RestSettings(3, 15));

                InvokePrivate(window, "EnterRevisionMode", commitment);
                var arguments = new object?[] { null, null };
                var succeeded = (bool)window.GetType()
                    .GetMethod("TryBuildDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, arguments)!;
                var roundTripped = Assert.IsType<CommitmentDraft>(arguments[0]);

                Assert.True(succeeded, Assert.IsType<string>(arguments[1]));
                Assert.Equal(start, roundTripped.StartAt);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    [Fact]
    public void Recurrence_adjustment_shows_reason_and_versions_but_skip_stays_a_status_fact()
    {
        var commitmentId = Guid.NewGuid();
        var before = DateTimeOffset.Parse("2026-08-14T08:00:00Z");
        var adjustment = RecurrenceChangeSummary.Format(new RecurrenceChangeCard(
            Guid.NewGuid(), Guid.NewGuid(), RecurrenceChangeKind.Adjust,
            RecurrenceChangeScope.ThisOccurrence,
            [new RecurrenceChangeOccurrencePreview(
                commitmentId, new DateOnly(2026, 8, 14), before, before.AddHours(1),
                RecurrenceOccurrenceStatus.Active, before.AddHours(1), before.AddHours(2),
                RecurrenceOccurrenceStatus.Active, 2, 3)],
            "尚未写入。", "临时会议冲突"));

        Assert.Contains("调整原因：临时会议冲突", adjustment);
        Assert.Contains("v2 → v3", adjustment);

        var skip = RecurrenceChangeSummary.Format(new RecurrenceChangeCard(
            Guid.NewGuid(), Guid.NewGuid(), RecurrenceChangeKind.Skip,
            RecurrenceChangeScope.ThisOccurrence,
            [new RecurrenceChangeOccurrencePreview(
                commitmentId, new DateOnly(2026, 8, 14), before, before.AddHours(1),
                RecurrenceOccurrenceStatus.Active, before, before.AddHours(1),
                RecurrenceOccurrenceStatus.Skipped)],
            "尚未写入。"));

        Assert.Contains("Active → 已跳过", skip);
        Assert.DoesNotContain("调整原因", skip);
        Assert.DoesNotContain("v1 → v1", skip);
    }

    private static CommitmentCard Card(string input, string outcome, int durationMinutes)
    {
        var start = DateTimeOffset.Parse("2026-08-12T08:00:00Z");
        return new CommitmentCard(
            Guid.NewGuid(), CommitmentKind.Computer, start, start.AddMinutes(durationMinutes),
            input, outcome,
            [new CommitmentTarget(CommitmentTargetKind.Application, "Word.exe")],
            SupervisionMode.Interactive,
            new ReminderSettings(true, 5, 20, 20, 3),
            "等待确认",
            [new ActivityRule(
                new CommitmentTarget(CommitmentTargetKind.Application, "Word.exe"),
                ActivityClassification.Related)],
            new RestSettings(10, 15));
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF regression test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void InvokePrivate(object instance, string methodName, params object[] arguments) =>
        instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, arguments);

    private static void SetPrivateField<T>(object instance, string fieldName, T value) =>
        instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private static T? GetPrivateField<T>(object instance, string fieldName) where T : class =>
        instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance) as T;
}
