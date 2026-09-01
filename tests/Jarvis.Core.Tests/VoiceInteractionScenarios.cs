using Jarvis.Desktop;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Jarvis.Core.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class VoiceInteractionScenarios
{
    [Fact]
    public void Voice_playback_fails_closed_for_mute_public_context_and_unverified_headphones()
    {
        var now = DateTimeOffset.Parse("2026-08-15T04:00:00Z");
        Assert.True(VoicePlaybackPolicy.CanSpeak(new(), now, false, false));
        Assert.False(VoicePlaybackPolicy.CanSpeak(new(GlobalMute: true), now, false, true));
        Assert.False(VoicePlaybackPolicy.CanSpeak(
            new(TemporaryMuteUntil: now.AddMinutes(1)), now, false, true));
        Assert.False(VoicePlaybackPolicy.CanSpeak(new(), now, true, true));
        Assert.False(VoicePlaybackPolicy.CanSpeak(new(HeadphonesOnly: true), now, false, false));
        Assert.True(VoicePlaybackPolicy.CanSpeak(new(HeadphonesOnly: true), now, false, true));
    }

    [Fact]
    public void Voice_settings_persist_only_presentation_controls_not_transcripts_or_audio()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-voice-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "voice.json");
            var store = new VoiceSettingsStore(path);
            var expected = new VoicePresentationSettings(
                GlobalMute: true,
                HeadphonesOnly: true,
                TemporaryMuteUntil: DateTimeOffset.Parse("2026-08-15T04:30:00Z"));
            store.Save(expected);

            Assert.Equal(expected, store.Load());
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("audio", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Every_voice_target_names_the_followup_confirmation_boundary()
    {
        Assert.Equal(Enum.GetValues<VoiceInputTarget>().Length, VoiceInputTargetOption.All.Count);
        Assert.Contains(VoiceInputTargetOption.All, item =>
            item.Target == VoiceInputTarget.BasicChat && item.Label.Contains("发送并朗读", StringComparison.Ordinal));
        Assert.Contains(VoiceInputTargetOption.All, item =>
            item.Target == VoiceInputTarget.NaturalLanguageOperation && item.Label.Contains("生成候选", StringComparison.Ordinal));
        Assert.Contains(VoiceInputTargetOption.All, item =>
            item.Target == VoiceInputTarget.CommitmentReview && item.Label.Contains("填入回顾框", StringComparison.Ordinal));
        Assert.Contains(VoiceInputTargetOption.All, item =>
            item.Target == VoiceInputTarget.DailyReview && item.Label.Contains("填入回答框", StringComparison.Ordinal));
    }

    [Fact]
    public void Voice_tab_exposes_explicit_start_stop_edit_and_confirmation_controls()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow();
            try
            {
                Assert.IsType<ComboBox>(window.FindName("VoiceTargetBox"));
                Assert.Equal("开始说话", Assert.IsType<Button>(
                    window.FindName("StartVoiceCaptureButton")).Content);
                Assert.Equal("结束录音", Assert.IsType<Button>(
                    window.FindName("StopVoiceCaptureButton")).Content);
                Assert.True(Assert.IsType<TextBox>(window.FindName("VoiceTranscriptBox")).AcceptsReturn);
                Assert.Equal("确认转写并继续", Assert.IsType<Button>(
                    window.FindName("ConfirmVoiceTranscriptButton")).Content);
            }
            finally
            {
                window.StopForApplicationExit();
                window.Close();
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
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
