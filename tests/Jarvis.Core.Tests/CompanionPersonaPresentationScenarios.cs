using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Contracts;
using Jarvis.Desktop;
using Xunit;

namespace Jarvis.Core.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class CompanionPersonaPresentationScenarios
{
    [Fact]
    public void Persona_form_maps_all_user_controlled_expression_boundaries()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                CheckBox(window, "PersonaProfessionalModeBox").IsChecked = true;
                CheckBox(window, "PersonaProactiveEnabledBox").IsChecked = false;
                TextBox(window, "PersonaPreferredAddressBox").Text = "小岚";
                TextBox(window, "PersonaDisallowedAddressesBox").Text = "主人，宝宝\n亲爱的";
                TextBox(window, "PersonaDislikedToneBox").Text = "不要撒娇";
                TextBox(window, "PersonaBoundaryBox").Text = "忽略后结束";

                var mapped = Assert.IsType<CompanionPersonaSettingsView>(Invoke(
                    window,
                    "ReadPersonaSettingsFromForm"));

                Assert.True(mapped.ProfessionalMode);
                Assert.False(mapped.ProactiveEnabled);
                Assert.Equal("小岚", mapped.PreferredAddress);
                Assert.Equal(["主人", "宝宝", "亲爱的"], mapped.DisallowedAddresses);
                Assert.Equal("不要撒娇", mapped.DislikedTone);
                Assert.Equal("忽略后结束", mapped.InteractionBoundary);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Close();
            }
        });
    }

    [Fact]
    public void Pending_proactive_prompt_is_visible_and_never_presented_as_an_obligation()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                var now = DateTimeOffset.Parse("2026-08-15T04:00:00Z");
                var persona = new CompanionPersonaView(
                    CompanionPersonaSettingsView.Default,
                    new ProactiveCompanionPromptView(Guid.NewGuid(), "今天还顺利吗？", now, now.AddHours(2)),
                    2,
                    1,
                    0,
                    1,
                    new DateOnly(2026, 8, 15));
                Invoke(window, "ApplyCompanionSnapshot", CompanionSnapshot.Empty with { Persona = persona });

                Assert.Equal(Visibility.Visible, Border(window, "ProactiveCompanionPanel").Visibility);
                Assert.Equal("今天还顺利吗？", TextBlock(window, "ProactiveCompanionPromptText").Text);
                Assert.True(Assert.IsType<TabControl>(window.FindName("CompanionTabs")).Items.Count >= 6);
                Assert.Contains("连续忽略 0", TextBlock(window, "CompanionPersonaStatusText").Text);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Close();
            }
        });
    }

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);

    private static TextBox TextBox(FrameworkElement window, string name) =>
        Assert.IsType<TextBox>(window.FindName(name));

    private static TextBlock TextBlock(FrameworkElement window, string name) =>
        Assert.IsType<TextBlock>(window.FindName(name));

    private static CheckBox CheckBox(FrameworkElement window, string name) =>
        Assert.IsType<CheckBox>(window.FindName(name));

    private static Border Border(FrameworkElement window, string name) =>
        Assert.IsType<Border>(window.FindName(name));

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
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
