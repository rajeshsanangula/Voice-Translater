using VTTranslate.Core.Providers;
using VTTranslate.Core.Session;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Covers Part 3 (device hot-plug resilience): a capture or playback device error
/// (e.g. Bluetooth/USB disconnect) must surface as a clear fatal error through
/// DirectionPipeline.Error — previously IAudioOutputSink had no error channel at all,
/// so a disappearing playback device failed silently.
/// </summary>
public class DirectionPipelineDeviceResilienceTests
{
    private static (DirectionPipeline Pipeline, FakeAudioInputSource Capture, FakeAudioOutputSink Playback, FakeSpeechTranslationProvider Provider) Build()
    {
        var session = new TranslationSession
        {
            Direction = SessionDirection.EnglishMicToGerman,
            SourceLanguage = "en-US",
            TargetLanguage = "de-DE"
        };
        var capture = new FakeAudioInputSource();
        var playback = new FakeAudioOutputSink();
        var provider = new FakeSpeechTranslationProvider();
        var pipeline = new DirectionPipeline(session, capture, playback, provider);
        return (pipeline, capture, playback, provider);
    }

    [Fact]
    public void CaptureError_SurfacesAsFatalPipelineError()
    {
        var (pipeline, capture, _, _) = Build();
        ProviderError? observed = null;
        pipeline.Error += (_, e) => observed = e;

        capture.EmitError(new InvalidOperationException("Bluetooth microphone disconnected"));

        Assert.NotNull(observed);
        Assert.True(observed!.IsFatal);
        Assert.Contains("Bluetooth microphone disconnected", observed.Message);
    }

    [Fact]
    public void PlaybackError_SurfacesAsFatalPipelineError()
    {
        var (pipeline, _, playback, _) = Build();
        ProviderError? observed = null;
        pipeline.Error += (_, e) => observed = e;

        playback.EmitPlaybackError(new InvalidOperationException("Bluetooth headphones disconnected"));

        Assert.NotNull(observed);
        Assert.True(observed!.IsFatal);
        Assert.Contains("Bluetooth headphones disconnected", observed.Message);
    }

    [Fact]
    public void PlaybackError_SetsSessionStatusToErrorAndRecordsLastError()
    {
        var (pipeline, _, playback, _) = Build();

        playback.EmitPlaybackError(new InvalidOperationException("device gone"));

        Assert.Equal(SessionStatus.Error, pipeline.Session.Status);
        Assert.Contains("device gone", pipeline.Session.LastError);
    }

    [Fact]
    public async Task DeviceErrorDuringSession_DoesNotPreventCleanStopAfterward()
    {
        // Confirms the fail-safe design: a fatal device error leaves the pipeline in a
        // state where an explicit Stop still works cleanly (no crash, no hang) — this
        // is what the caller (MainViewModel) relies on to guarantee no duplicate
        // streams can be created by a later Start.
        var (pipeline, capture, playback, provider) = Build();

        capture.EmitError(new InvalidOperationException("mic gone"));
        await pipeline.StopAsync();

        Assert.Equal(1, capture.StopCallCount);
        Assert.Equal(1, provider.StopAsyncCallCount);
        Assert.Equal(1, playback.StopCallCount);
    }
}
