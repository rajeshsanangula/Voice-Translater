using VTTranslate.Core.Providers;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Test double for ISpeechTranslationProvider (+ IReconnectingProvider) — lets tests
/// drive Partial/Final/Synthesized/Error/StatusChanged events directly, synchronously,
/// with no Azure connection at all. Also records lifecycle calls (StartAsync/StopAsync/
/// DisposeAsync/PushAudio) so tests can assert mute never touches them.
/// </summary>
internal sealed class FakeSpeechTranslationProvider : ISpeechTranslationProvider, IReconnectingProvider
{
    public event EventHandler<TranslationResult>? PartialResult;
    public event EventHandler<TranslationResult>? FinalResult;
    public event EventHandler<SynthesizedAudio>? AudioSynthesized;
    public event EventHandler<ProviderError>? Error;
    public event EventHandler<string>? StatusChanged;

    public int StartAsyncCallCount { get; private set; }
    public int StopAsyncCallCount { get; private set; }
    public int DisposeAsyncCallCount { get; private set; }
    public List<byte[]> PushedAudio { get; } = new();

    public Task StartAsync(string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        StartAsyncCallCount++;
        return Task.CompletedTask;
    }

    public void PushAudio(ReadOnlySpan<byte> pcm16) => PushedAudio.Add(pcm16.ToArray());

    public Task StopAsync()
    {
        StopAsyncCallCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeAsyncCallCount++;
        return ValueTask.CompletedTask;
    }

    public void EmitPartial(string text) => PartialResult?.Invoke(this, new TranslationResult(text, "", false, TimeSpan.Zero));
    public void EmitFinal(string text) => FinalResult?.Invoke(this, new TranslationResult(text, "", true, TimeSpan.Zero));
    public void EmitSynthesized(byte[] pcm) => AudioSynthesized?.Invoke(this, new SynthesizedAudio(pcm, 16000));
    public void EmitError(string message, bool fatal) => Error?.Invoke(this, new ProviderError(message, fatal));
    public void EmitStatusChanged(string status) => StatusChanged?.Invoke(this, status);
}
