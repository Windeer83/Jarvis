using Jarvis.Contracts;
using Jarvis.Desktop;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using Xunit;

namespace Jarvis.Core.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class DesktopPetScenarios
{
    [Fact]
    public void Projection_uses_authoritative_supervision_states()
    {
        var now = DateTimeOffset.Parse("2026-08-15T08:00:00Z");
        var commitment = Commitment(now);
        var supervision = Snapshot(
            now,
            commitment,
            new ActiveSupervisionView(
                commitment.Id,
                ActivityClassification.Related,
                false,
                null,
                null,
                TimeSpan.Zero,
                now,
                false,
                null,
                null,
                null,
                null,
                null,
                []));

        var working = DesktopPetProjectionBuilder.Build(supervision, CompanionSnapshot.Empty, now);
        Assert.Equal(DesktopPetVisualState.Working, working.VisualState);
        Assert.Contains("交易复盘", working.Detail);

        var reminderState = supervision.ActiveSupervision! with
        {
            Classification = ActivityClassification.Distracting,
            DeviationStartedAt = now.AddMinutes(-6),
            CountedDeviation = TimeSpan.FromMinutes(6),
            ReminderMarkerActive = true
        };
        var reminder = DesktopPetProjectionBuilder.Build(
            supervision with { ActiveSupervision = reminderState }, CompanionSnapshot.Empty, now);
        Assert.Equal(DesktopPetVisualState.Reminder, reminder.VisualState);
        Assert.Contains("连续偏离", reminder.Detail);

        var rest = DesktopPetProjectionBuilder.Build(
            supervision with
            {
                ActiveSupervision = reminderState with
                {
                    ReminderMarkerActive = false,
                    ActiveRest = new TimedRestView(now, now.AddMinutes(15), TimedRestSource.Proactive)
                }
            },
            CompanionSnapshot.Empty,
            now);
        Assert.Equal(DesktopPetOverlayState.Resting, rest.OverlayState);
        Assert.Contains("自动恢复监督", rest.Detail);
    }

    [Fact]
    public void Unknown_and_recent_completion_have_distinct_caring_and_happy_states()
    {
        var now = DateTimeOffset.Parse("2026-08-15T08:00:00Z");
        var commitment = Commitment(now);
        var unknown = Snapshot(
            now,
            commitment,
            new ActiveSupervisionView(
                commitment.Id,
                ActivityClassification.Unknown,
                false,
                DeviationReason.UnknownActivity,
                now.AddSeconds(-30),
                TimeSpan.FromSeconds(30),
                null,
                false,
                null,
                null,
                null,
                null,
                null,
                []));
        Assert.Equal(
            DesktopPetVisualState.Caring,
            DesktopPetProjectionBuilder.Build(unknown, CompanionSnapshot.Empty, now).VisualState);

        var completed = CompanionSnapshot.Empty with
        {
            CommitmentReviews =
            [
                new CommitmentReviewView(
                    commitment.Id,
                    1,
                    CommitmentReviewState.Completed,
                    now.AddMinutes(-2),
                    RawText: "按计划完成",
                    Assessment: CompletionAssessment.Completed,
                    AnsweredAt: now.AddMinutes(-1))
            ]
        };
        var idle = new SupervisionSnapshot(now, null, [commitment with { Phase = CommitmentPhase.AwaitingReview }], null, null);
        Assert.Equal(
            DesktopPetVisualState.Happy,
            DesktopPetProjectionBuilder.Build(idle, completed, now).VisualState);
    }

    [Fact]
    public void Verified_local_backup_waiting_for_client_is_a_caring_local_only_notice()
    {
        var now = DateTimeOffset.Parse("2026-08-15T08:00:00Z");
        var supervision = new SupervisionSnapshot(now, null, [], null, null);
        var companion = CompanionSnapshot.Empty with
        {
            Backup = new BackupStatusView(
                @"D:\BaiduSync\Jarvis", true, now.AddDays(-1),
                @"D:\BaiduSync\Jarvis\jarvis-daily.jarvis-backup", now.AddDays(-1),
                false, "本地备份已验证，但百度网盘客户端连续 24 小时未运行；云端状态未知。",
                true, null)
        };

        var projection = DesktopPetProjectionBuilder.Build(supervision, companion, now);

        Assert.Equal(DesktopPetVisualState.Caring, projection.VisualState);
        Assert.Contains("本地备份", projection.Status);
        Assert.Contains("云端状态未知", projection.Detail);
    }

    [Fact]
    public void Position_snap_stays_inside_the_current_monitor()
    {
        Assert.Equal(
            (0d, 230d),
            DesktopPetSnap.ConstrainAndSnap(-50, 230, 200, 300, 0, 0, 1000, 700));
        Assert.Equal(
            (800d, 390d),
            DesktopPetSnap.ConstrainAndSnap(790, 390, 200, 300, 0, 0, 1000, 700));
        Assert.Equal(
            (420d, 200d),
            DesktopPetSnap.ConstrainAndSnap(420, 200, 200, 300, 0, 0, 1000, 700));
    }

    [Fact]
    public void Settings_round_trip_normalizes_scale_and_process_names()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Jarvis-Pet-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "desktop-pet.json");
        try
        {
            var store = new DesktopPetSettingsStore(path);
            store.Save(new DesktopPetSettings(
                12,
                34,
                9,
                true,
                true,
                false,
                ["POWERPNT.EXE", "powerpnt", "  zoom.exe  "]));

            var loaded = store.Load();
            Assert.Equal(1.4, loaded.Scale);
            Assert.True(loaded.ClickThrough);
            Assert.Equal(["powerpnt", "zoom"], loaded.HiddenProcesses, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Pet_window_loads_embedded_state_assets()
    {
        RunOnStaThread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "Jarvis-Pet-Wpf-" + Guid.NewGuid().ToString("N"));
            try
            {
                var window = new DesktopPetWindow(new DesktopPetSettingsStore(Path.Combine(directory, "settings.json")));
                window.ApplyProjection(new DesktopPetProjection(
                    DesktopPetVisualState.Reminder,
                    DesktopPetOverlayState.None,
                    "回到承诺",
                    "状态资产测试"));
                var image = Assert.IsType<Image>(window.FindName("CharacterImage"));
                var badge = Assert.IsType<TextBlock>(window.FindName("StateBadgeText"));
                Assert.NotNull(image.Source);
                Assert.Equal("提醒", badge.Text);
                window.StopForApplicationExit();
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        });
    }

    private static CommitmentView Commitment(DateTimeOffset now) => new(
        Guid.NewGuid(),
        CommitmentKind.Computer,
        now.AddMinutes(-10),
        now.AddHours(1),
        "交易复盘",
        null,
        [new CommitmentTarget(CommitmentTargetKind.Application, "notion")],
        SupervisionMode.Interactive,
        new ReminderSettings(true, 5, 20, 20, 3),
        CommitmentPhase.Supervising,
        now.AddMinutes(-20),
        null,
        [new ActivityRule(new CommitmentTarget(CommitmentTargetKind.Application, "notion"), ActivityClassification.Related)],
        new RestSettings(10, 15));

    private static SupervisionSnapshot Snapshot(
        DateTimeOffset now,
        CommitmentView commitment,
        ActiveSupervisionView state) => new(
        now,
        commitment.Id,
        [commitment],
        new ActivityObservation(ActivityAvailability.Available, true, "notion", now),
        null,
        state);

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
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
