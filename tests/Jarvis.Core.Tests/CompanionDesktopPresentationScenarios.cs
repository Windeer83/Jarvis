using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using Jarvis.Contracts;
using Jarvis.Desktop;
using Xunit;

namespace Jarvis.Core.Tests;

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
