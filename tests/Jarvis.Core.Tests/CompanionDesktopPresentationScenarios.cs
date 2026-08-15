using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using Jarvis.Contracts;
using Jarvis.Desktop;
using Xunit;

namespace Jarvis.Core.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class CompanionDesktopPresentationScenarios
{
    [Fact]
    public void Sole_pending_review_is_selected_and_resolves_without_the_distant_commitment_grid_selection()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var now = DateTimeOffset.Parse("2026-08-15T08:00:00Z");
                var commitment = new CommitmentView(
                    Guid.NewGuid(), CommitmentKind.Computer, now.AddMinutes(-25), now,
                    "交易的复盘", null,
                    [new CommitmentTarget(CommitmentTargetKind.Application, "notion")],
                    SupervisionMode.Interactive,
                    new ReminderSettings(true, 2, 5, 5, 3),
                    CommitmentPhase.AwaitingReview, now.AddMinutes(-30), null,
                    [], new RestSettings(3, 15), Version: 2);
                var review = new CommitmentReviewView(
                    commitment.Id, commitment.Version, CommitmentReviewState.Pending, now);

                InvokePrivate(window, "ApplySnapshot", new SupervisionSnapshot(
                    now, null, [commitment], null, null));
                InvokePrivate(window, "ApplyCompanionSnapshot", CompanionSnapshot.Empty with
                {
                    CommitmentReviews = [review]
                });

                var reviewList = (ListBox)window.FindName("CommitmentReviewList");
                Assert.Equal(0, reviewList.SelectedIndex);
                var selected = Assert.IsType<CommitmentView>(
                    InvokePrivateWithResult(window, "ResolveSelectedReviewCommitment"));
                Assert.Equal(commitment.Id, selected.Id);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    [Fact]
    public void Ready_feishu_status_explains_cli_profile_and_when_a_phone_message_is_sent()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivate(window, "ApplyCompanionSnapshot", CompanionSnapshot.Empty with
                {
                    WorktimeChannel = new WorktimeChannelView(
                        true, true, true, "jarvis-t04", "55ad", null)
                });

                var status = ((TextBlock)window.FindName("WorktimeStatusText")).Text;
                Assert.Contains("连续偏离", status, StringComparison.Ordinal);
                Assert.Contains("不会在监督开始或结束时自动发送", status, StringComparison.Ordinal);
                Assert.Contains("本机飞书命令工具", ((TextBlock)window.FindName("LarkCliHelpText")).Text);
                Assert.Contains("授权配置名", ((TextBlock)window.FindName("LarkProfileHelpText")).Text);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    [Fact]
    public void Core_snapshot_refresh_preserves_the_selected_commitment()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var now = DateTimeOffset.Parse("2026-08-15T08:00:00Z");
                var commitment = Commitment(now);
                InvokePrivate(window, "ApplySnapshot", new SupervisionSnapshot(
                    now, null, [commitment], null, null));
                var grid = (DataGrid)window.FindName("CommitmentGrid");
                grid.SelectedIndex = 0;

                InvokePrivate(window, "ApplySnapshot", new SupervisionSnapshot(
                    now.AddSeconds(2), null, [commitment with { Version = 2 }], null, null));

                Assert.Equal(0, grid.SelectedIndex);
                var selected = Assert.IsType<CommitmentView>(
                    InvokePrivateWithResult(window, "SelectedCommitment"));
                Assert.Equal(commitment.Id, selected.Id);
                Assert.Equal(2, selected.Version);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    [Fact]
    public void Completed_review_is_visible_next_to_the_supervision_phase()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var now = DateTimeOffset.Parse("2026-08-15T08:00:00Z");
                var commitment = Commitment(now);
                InvokePrivate(window, "ApplySnapshot", new SupervisionSnapshot(
                    now, null, [commitment], null, null));
                InvokePrivate(window, "ApplyCompanionSnapshot", CompanionSnapshot.Empty with
                {
                    CommitmentReviews =
                    [
                        new CommitmentReviewView(
                            commitment.Id, commitment.Version, CommitmentReviewState.Completed, now,
                            RawText: "完成了交易复盘", Assessment: CompletionAssessment.Completed,
                            AnsweredAt: now)
                    ]
                });

                var grid = (DataGrid)window.FindName("CommitmentGrid");
                grid.SelectedIndex = 0;
                Assert.NotNull(grid.SelectedItem);
                var row = grid.SelectedItem;
                var reviewStatus = row.GetType().GetProperty("ReviewStatus")?.GetValue(row)?.ToString();
                var supervisionStatus = row.GetType().GetProperty("SupervisionStatus")?.GetValue(row)?.ToString();
                Assert.Equal("监督已结束", supervisionStatus);
                Assert.Contains("已回顾", reviewStatus, StringComparison.Ordinal);
                Assert.Contains("已完成", reviewStatus, StringComparison.Ordinal);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    [Fact]
    public void Mouse_wheel_over_core_grid_continues_scrolling_the_outer_page()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                window.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("ContentScrollViewer");
                Assert.True(scroll.ScrollableHeight > 120);
                scroll.ScrollToTop();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var before = scroll.VerticalOffset;
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent
                };

                InvokePrivate(window, "CommitmentGrid_PreviewMouseWheel", window, args);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.True(args.Handled);
                Assert.True(scroll.VerticalOffset > before);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    [Fact]
    public void Completed_daily_review_keeps_its_raw_answers_visible_in_desktop()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var at = DateTimeOffset.Parse("2026-08-15T15:00:00Z");
                var sessionId = Guid.NewGuid();
                var review = new DailyReviewView(
                    ReviewSessionState.Completed, new TimeOnly(23, 0), sessionId,
                    new DateOnly(2026, 8, 15), StructuredAnswers:
                    [
                        new DailyReviewAnswerView(ReviewQuestionKind.Facts, "完成了交易复盘", at),
                        new DailyReviewAnswerView(ReviewQuestionKind.TomorrowAdjustments, "明天先检查交易计划", at.AddMinutes(1))
                    ]);

                InvokePrivate(window, "ApplyCompanionSnapshot", CompanionSnapshot.Empty with
                {
                    DailyReview = review
                });

                var record = ((TextBlock)window.FindName("DailyReviewRecordText")).Text;
                Assert.Contains("2026-08-15", record, StringComparison.Ordinal);
                Assert.Contains("完成了交易复盘", record, StringComparison.Ordinal);
                Assert.Contains("明天先检查交易计划", record, StringComparison.Ordinal);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Hide();
            }
        });
    }

    private static CommitmentView Commitment(DateTimeOffset now) => new(
        Guid.NewGuid(), CommitmentKind.Computer, now.AddMinutes(-25), now,
        "交易的复盘", null,
        [new CommitmentTarget(CommitmentTargetKind.Application, "notion")],
        SupervisionMode.Interactive,
        new ReminderSettings(true, 2, 5, 5, 3),
        CommitmentPhase.AwaitingReview, now.AddMinutes(-30), null,
        [], new RestSettings(3, 15));

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
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void InvokePrivate(object instance, string methodName, params object[] arguments) =>
        instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, arguments);

    private static object? InvokePrivateWithResult(object instance, string methodName, params object[] arguments) =>
        instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, arguments);
}
