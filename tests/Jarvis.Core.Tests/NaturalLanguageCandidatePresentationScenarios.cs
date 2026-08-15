using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Contracts;
using Jarvis.Desktop;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class NaturalLanguageCandidatePresentationScenarios
{
    [Fact]
    public void Missing_information_is_shown_next_to_the_candidate_input_as_an_actionable_list()
    {
        var outcome = new CompanionOutcome(
            false,
            "ai_clarification_required",
            "还不能生成候选，请补充必要信息。",
            MissingInformation:
            [
                "监督结束时间或持续时长",
                "投入目标或成果目标（二选一即可）"
            ]);

        var text = NaturalLanguageCandidatePresentation.FormatFailure(outcome);

        Assert.Contains("还不能生成候选", text, StringComparison.Ordinal);
        Assert.Contains("• 监督结束时间或持续时长", text, StringComparison.Ordinal);
        Assert.Contains("• 投入目标或成果目标（二选一即可）", text, StringComparison.Ordinal);
        Assert.Contains("补充后再次点击“生成候选操作”", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Busy_message_explains_that_no_formal_supervision_has_started()
    {
        Assert.Equal(
            "正在生成候选操作，请稍后…\n尚未创建或启动正式监督。",
            NaturalLanguageCandidatePresentation.BusyText);
    }

    [Fact]
    public void Candidate_generation_immediately_shows_progress_and_blocks_duplicate_actions()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivate(window, "SetNaturalLanguageBusy", true);

                Assert.Equal(
                    Visibility.Visible,
                    ((StackPanel)window.FindName("NaturalLanguageBusyPanel")).Visibility);
                Assert.False(((Button)window.FindName("GenerateNaturalLanguageCandidateButton")).IsEnabled);
                Assert.False(((Button)window.FindName("ConfirmNaturalLanguageCandidateButton")).IsEnabled);
                Assert.False(((Button)window.FindName("DiscardNaturalLanguageCandidateButton")).IsEnabled);
                Assert.Equal(
                    NaturalLanguageCandidatePresentation.BusyText,
                    ((TextBlock)window.FindName("NaturalLanguageCandidateText")).Text);
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
}
