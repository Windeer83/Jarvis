using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text.Json;

namespace Jarvis.Desktop;

public enum VoiceInputTarget
{
    BasicChat,
    NaturalLanguageOperation,
    CommitmentReview,
    DailyReview
}

public sealed record VoiceInputTargetOption(VoiceInputTarget Target, string Label)
{
    public static IReadOnlyList<VoiceInputTargetOption> All { get; } =
    [
        new(VoiceInputTarget.BasicChat, "普通对话（确认后发送并朗读回复）"),
        new(VoiceInputTarget.NaturalLanguageOperation, "安排或修改工作（确认后生成候选）"),
        new(VoiceInputTarget.CommitmentReview, "承诺回顾（确认后填入回顾框）"),
        new(VoiceInputTarget.DailyReview, "每日复盘（确认后填入回答框）")
    ];
}

public sealed record VoiceTranscriptionResult(bool Success, string Text, string Message);

public sealed record VoicePlaybackResult(bool Success, string Message);

public sealed record VoicePresentationSettings(
    bool GlobalMute = false,
    bool HeadphonesOnly = false,
    DateTimeOffset? TemporaryMuteUntil = null);

public static class VoicePlaybackPolicy
{
    public static bool CanSpeak(
        VoicePresentationSettings settings,
        DateTimeOffset now,
        bool privatePresentationSuppressed,
        bool verifiedHeadphones) =>
        !settings.GlobalMute &&
        !(settings.TemporaryMuteUntil is { } until && now < until) &&
        !privatePresentationSuppressed &&
        (!settings.HeadphonesOnly || verifiedHeadphones);
}

public sealed class VoiceSettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jarvis",
        "voice-presentation.json");

    public VoicePresentationSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<VoicePresentationSettings>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(VoicePresentationSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed class WindowsSpeechService : IDisposable
{
    private readonly object _sync = new();
    private SpeechRecognitionEngine? _recognizer;
    private TaskCompletionSource<VoiceTranscriptionResult>? _captureCompletion;
    private readonly List<string> _segments = [];
    private SpeechSynthesizer? _synthesizer;

    public bool IsListening
    {
        get
        {
            lock (_sync) return _recognizer is not null;
        }
    }

    public Task<VoiceTranscriptionResult> StartCaptureAsync()
    {
        lock (_sync)
        {
            if (_recognizer is not null)
            {
                return Task.FromResult(new VoiceTranscriptionResult(
                    false, "", "语音输入已经开始，请先结束当前录音。"));
            }

            try
            {
                var recognizerInfo = SpeechRecognitionEngine.InstalledRecognizers()
                    .FirstOrDefault(item => item.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) ??
                    SpeechRecognitionEngine.InstalledRecognizers()
                        .FirstOrDefault(item => item.Culture.TwoLetterISOLanguageName == "zh");
                if (recognizerInfo is null)
                {
                    return Task.FromResult(new VoiceTranscriptionResult(
                        false, "", "Windows 未安装普通话语音识别组件；请继续使用同页文字输入。"));
                }

                _segments.Clear();
                _captureCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _recognizer = new SpeechRecognitionEngine(recognizerInfo);
                _recognizer.LoadGrammar(new DictationGrammar());
                _recognizer.SetInputToDefaultAudioDevice();
                _recognizer.SpeechRecognized += RecognizerOnSpeechRecognized;
                _recognizer.RecognizeCompleted += RecognizerOnRecognizeCompleted;
                _recognizer.RecognizeAsync(RecognizeMode.Multiple);
                return _captureCompletion.Task;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or COMException)
            {
                CleanupRecognizer();
                return Task.FromResult(new VoiceTranscriptionResult(
                    false, "", $"无法启动麦克风语音识别：{exception.Message}。仍可使用文字输入。"));
            }
        }
    }

    public void StopCapture()
    {
        SpeechRecognitionEngine? recognizer;
        lock (_sync)
        {
            recognizer = _recognizer;
        }

        try
        {
            recognizer?.RecognizeAsyncStop();
        }
        catch (InvalidOperationException)
        {
            lock (_sync) CompleteCapture();
        }
    }

    public async Task<VoicePlaybackResult> SpeakAsync(
        string text,
        VoicePresentationSettings settings,
        bool privatePresentationSuppressed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(false, "没有可朗读的文字。");
        var outputName = DefaultAudioOutputName();
        var verifiedHeadphones = IsHeadphoneDevice(outputName);
        if (!VoicePlaybackPolicy.CanSpeak(
                settings, DateTimeOffset.Now, privatePresentationSuppressed, verifiedHeadphones))
        {
            return new(false, settings.HeadphonesOnly && !verifiedHeadphones
                ? "默认输出设备未能确认为耳机，本次保持文字显示且不朗读。"
                : "当前静音或公开场合边界阻止了朗读；文字仍完整显示。");
        }

        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var synthesizer = new SpeechSynthesizer();
                lock (_sync) _synthesizer = synthesizer;
                var voices = synthesizer.GetInstalledVoices()
                    .Where(item => item.Enabled)
                    .Select(item => item.VoiceInfo)
                    .ToArray();
                var voice = voices.FirstOrDefault(item =>
                                item.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) &&
                                item.Gender == VoiceGender.Female && item.Age == VoiceAge.Adult) ??
                            voices.FirstOrDefault(item =>
                                item.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) &&
                                item.Gender == VoiceGender.Female) ??
                            voices.FirstOrDefault(item =>
                                item.Culture.TwoLetterISOLanguageName == "zh" && item.Gender == VoiceGender.Female) ??
                            voices.FirstOrDefault(item => item.Culture.TwoLetterISOLanguageName == "zh");
                if (voice is not null) synthesizer.SelectVoice(voice.Name);
                synthesizer.Rate = -1;
                synthesizer.Volume = 70;
                synthesizer.SetOutputToDefaultAudioDevice();
                var prompt = synthesizer.SpeakAsync(text);
                while (!prompt.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Sleep(50);
                }
            }, cancellationToken).ConfigureAwait(false);
            return new(true, "语音回应已播放；相同内容仍保留在文字区。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or COMException)
        {
            return new(false, $"语音播放失败：{exception.Message}。文字内容仍可查看。");
        }
        finally
        {
            lock (_sync) _synthesizer = null;
        }
    }

    public void StopSpeaking()
    {
        lock (_sync)
        {
            try
            {
                _synthesizer?.SpeakAsyncCancelAll();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public void Dispose()
    {
        StopCapture();
        StopSpeaking();
        lock (_sync)
        {
            var completion = _captureCompletion;
            CleanupRecognizer();
            completion?.TrySetResult(new VoiceTranscriptionResult(
                false, "", "语音输入已随应用退出而结束；没有保存原始录音。"));
        }
    }

    private void RecognizerOnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Result.Text))
        {
            lock (_sync) _segments.Add(eventArgs.Result.Text.Trim());
        }
    }

    private void RecognizerOnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs eventArgs)
    {
        lock (_sync) CompleteCapture(eventArgs.Error);
    }

    private void CompleteCapture(Exception? error = null)
    {
        var completion = _captureCompletion;
        var text = string.Join("，", _segments.Where(value => value.Length > 0));
        CleanupRecognizer();
        completion?.TrySetResult(error is null && text.Length > 0
            ? new VoiceTranscriptionResult(true, text, "转写完成，请先核对或修改文字，再确认继续。")
            : new VoiceTranscriptionResult(false, text,
                error is null ? "没有识别到清晰语音；可以重试或直接输入文字。" :
                $"语音识别失败：{error.Message}。可以直接输入文字。"));
    }

    private void CleanupRecognizer()
    {
        if (_recognizer is not null)
        {
            _recognizer.SpeechRecognized -= RecognizerOnSpeechRecognized;
            _recognizer.RecognizeCompleted -= RecognizerOnRecognizeCompleted;
            _recognizer.Dispose();
        }
        _recognizer = null;
        _captureCompletion = null;
        _segments.Clear();
    }

    private static bool IsHeadphoneDevice(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        new[] { "headphone", "headset", "earphone", "耳机", "耳麦" }
            .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string? DefaultAudioOutputName()
    {
        var capabilities = new WaveOutCapabilities();
        return WaveOutGetDevCaps(unchecked((nuint)(-1)), ref capabilities,
                   (uint)Marshal.SizeOf<WaveOutCapabilities>()) == 0
            ? capabilities.ProductName
            : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCapabilities
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ProductName;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint WaveOutGetDevCaps(
        nuint deviceId,
        ref WaveOutCapabilities capabilities,
        uint capabilitiesSize);
}
