using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Tests the Step 3 shadow/observation integration boundary in isolation, with no Azure
/// dependency — <see cref="ShadowStabilityObserver"/> is a plain, DI-friendly class with
/// no events and no reference to translation/TTS/playback, so its correctness (and the
/// "shadow output never reaches production paths" guarantee) can be verified directly.
/// AzureSpeechTranslationProvider itself requires a real Azure connection to exercise its
/// Recognizing/Recognized handlers and has no existing unit-test coverage for that reason
/// (confirmed: no prior AzureSpeechTranslationProviderTests file in this project) — the
/// "shadow never reaches translation/TTS/playback" and "existing final-result/cancellation
/// paths unchanged" guarantees for that class are structural (ShadowStabilityObserver has
/// no way to invoke PartialResult/FinalResult/AudioSynthesized — verified by reading
/// AzureSpeechTranslationProvider.cs: the shadow calls are additive and their return
/// values are discarded) and by code diff (no line inside the existing FinalResult/
/// AudioSynthesized/Canceled logic was altered — see
/// docs/design-notes/streaming-shadow-integration.md §7 for the full mapping of these
/// requirements to what is/isn't executable-tested).
/// </summary>
public class ShadowStabilityObserverTests
{
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    // 1. Recognizing partial reaches the stability engine.
    [Fact]
    public void ObservePartial_PassesSourceTextToTheEngine_AndReturnsItsResult()
    {
        var logger = new InMemoryDiagnosticLogger();
        var observer = new ShadowStabilityObserver(logger, "en-US->de-DE", generation: 1, new PrefixStabilityEngine());

        observer.BeginUtterance("gen1-utt0", T(0));
        observer.ObservePartial(1, "I would", T(10));
        var r2 = observer.ObservePartial(2, "I would like", T(20));

        Assert.Equal(StabilityAction.Committed, r2.Action);
        Assert.Equal("I", r2.NewlyCommittedSegment);
    }

    // 12. Diagnostics contain metadata but not complete speech text.
    [Fact]
    public void ObservePartial_NeverLogsRecognizedSourceText_OnlyMetadata()
    {
        var logger = new InMemoryDiagnosticLogger();
        var observer = new ShadowStabilityObserver(logger, "en-US->de-DE", generation: 1, new PrefixStabilityEngine());
        const string sensitive = "I would like to schedule a very specific confidential meeting";

        observer.BeginUtterance("gen1-utt0", T(0));
        observer.ObservePartial(1, sensitive, T(10));
        observer.ObservePartial(2, sensitive + " tomorrow", T(20));

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive));
        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains("would") || e.Details.Contains("schedule"));
        // Required metadata fields are present.
        var partialLine = logger.Entries.Single(e => e.EventType == "ShadowPartialObserved" && e.Details.Contains("partialSequence=2"));
        Assert.Contains("generation=1", partialLine.Details);
        Assert.Contains("utteranceId=gen1-utt0", partialLine.Details);
        Assert.Contains("proposedCommitTokenCount=", partialLine.Details);
        Assert.Contains("proposedCommitCharCount=", partialLine.Details);
        Assert.Contains("cumulativeCommittedTokenCount=", partialLine.Details);
        Assert.Contains("shouldEmit=", partialLine.Details);
    }

    [Fact]
    public void ObserveFinal_NeverLogsRecognizedSourceText_LogsFinalizationMetadata()
    {
        var logger = new InMemoryDiagnosticLogger();
        var observer = new ShadowStabilityObserver(logger, "de-DE->en-US", generation: 1, new PrefixStabilityEngine());
        const string sensitive = "Ich möchte morgen ein vertrauliches Treffen vereinbaren";

        observer.BeginUtterance("gen1-utt0", T(0));
        observer.ObserveFinal(1, sensitive, T(50));

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive) || e.Details.Contains("Treffen"));
        var finalLine = logger.Entries.Single(e => e.EventType == "ShadowFinalized");
        Assert.Contains("finalized=true", finalLine.Details);
        Assert.Contains("finalizedWithCorrection=", finalLine.Details);
        Assert.Contains("elapsedT0ToFinalShadowFlushMs=", finalLine.Details);
    }

    // 5. Final recognition finalizes the shadow engine. 6. Engine resets after final. 7. New utterance starts clean.
    [Fact]
    public void ObserveFinal_FinalizesEngine_ThenNextUtteranceStartsClean()
    {
        var logger = new InMemoryDiagnosticLogger();
        var engine = new PrefixStabilityEngine();
        var observer = new ShadowStabilityObserver(logger, "en-US->de-DE", generation: 1, engine);

        observer.BeginUtterance("gen1-utt0", T(0));
        observer.ObservePartial(1, "hello", T(10));
        observer.ObservePartial(2, "hello world", T(20));
        var final1 = observer.ObserveFinal(3, "hello world.", T(30));
        Assert.Equal("hello world.", final1.CommittedSourceText);

        // New utterance: BeginUtterance again, first partial must start from a clean engine
        // (no leftover committed text from utterance 1).
        observer.BeginUtterance("gen1-utt1", T(100));
        var r = observer.ObservePartial(1, "second", T(110));
        Assert.Equal("", r.CommittedSourceText); // clean — nothing carried over
    }

    // ObserveFinal/ObservePartial without BeginUtterance must fail loudly, not silently corrupt state.
    [Fact]
    public void ObservePartial_WithoutBeginUtterance_Throws()
    {
        var observer = new ShadowStabilityObserver(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1, new PrefixStabilityEngine());
        Assert.Throws<InvalidOperationException>(() => observer.ObservePartial(1, "hi", T(0)));
    }

    [Fact]
    public void ObserveFinal_WithoutBeginUtterance_Throws()
    {
        var observer = new ShadowStabilityObserver(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1, new PrefixStabilityEngine());
        Assert.Throws<InvalidOperationException>(() => observer.ObserveFinal(1, "hi", T(0)));
    }

    // Zero-partial utterance (short utterance, straight to Final) must still finalize cleanly.
    [Fact]
    public void ObserveFinal_WithNoPriorPartials_StillFinalizesFully()
    {
        var logger = new InMemoryDiagnosticLogger();
        var observer = new ShadowStabilityObserver(logger, "de-DE->en-US", 1, new PrefixStabilityEngine());

        observer.BeginUtterance("gen1-utt0", T(0));
        var final = observer.ObserveFinal(1, "Ja.", T(5));

        Assert.Equal("Ja.", final.CommittedSourceText);
        Assert.True(final.ShouldEmit);
    }

    // 8 & 9. Reconnect/generation change isolates old partial state — modeled here as two
    // independent ShadowStabilityObserver instances (exactly how AzureSpeechTranslationProvider
    // creates one per generation), proving one's state cannot affect the other's.
    [Fact]
    public void SeparateGenerationInstances_NeverShareOrContaminateState()
    {
        var logger = new InMemoryDiagnosticLogger();
        var genOneEngine = new PrefixStabilityEngine();
        var genOneObserver = new ShadowStabilityObserver(logger, "en-US->de-DE", generation: 1, genOneEngine);
        genOneObserver.BeginUtterance("gen1-utt0", T(0));
        genOneObserver.ObservePartial(1, "old generation text", T(10));
        genOneObserver.ObservePartial(2, "old generation text growing", T(20));

        // A "reconnect" in production creates a brand-new ShadowStabilityObserver (and a
        // brand-new engine) for the new generation — old generation's observer/engine are
        // simply never touched again (its closure is dropped). Simulate that here.
        var genTwoEngine = new PrefixStabilityEngine();
        var genTwoObserver = new ShadowStabilityObserver(logger, "en-US->de-DE", generation: 2, genTwoEngine);
        genTwoObserver.BeginUtterance("gen2-utt0", T(1000));
        var r = genTwoObserver.ObservePartial(1, "brand new utterance", T(1010));

        Assert.Equal("", r.CommittedSourceText); // no trace of generation 1's content
        Assert.Contains(logger.Entries, e => e.Details.Contains("generation=1"));
        Assert.Contains(logger.Entries, e => e.Details.Contains("generation=2"));
    }

    // 13. Timestamps/sequence numbers remain deterministic where applicable.
    [Fact]
    public void SameInputSequence_ProducesIdenticalResults_RegardlessOfWhichWallClockValuesAreUsed()
    {
        var loggerA = new InMemoryDiagnosticLogger();
        var observerA = new ShadowStabilityObserver(loggerA, "en-US->de-DE", 1, new PrefixStabilityEngine());
        observerA.BeginUtterance("u", T(0));
        var a1 = observerA.ObservePartial(1, "I would", T(5));
        var a2 = observerA.ObservePartial(2, "I would like", T(9));

        var loggerB = new InMemoryDiagnosticLogger();
        var observerB = new ShadowStabilityObserver(loggerB, "en-US->de-DE", 1, new PrefixStabilityEngine());
        observerB.BeginUtterance("u", T(100_000));
        var b1 = observerB.ObservePartial(1, "I would", T(999_999));
        var b2 = observerB.ObservePartial(2, "I would like", T(1_234_567));

        Assert.Equal(a1.NewlyCommittedSegment, b1.NewlyCommittedSegment);
        Assert.Equal(a2.NewlyCommittedSegment, b2.NewlyCommittedSegment);
        Assert.Equal(a2.CommittedSourceText, b2.CommittedSourceText);
    }

    // First-partial and first-commit one-shot diagnostics fire exactly once per utterance.
    [Fact]
    public void FirstPartialAndFirstCommitDiagnostics_LogExactlyOncePerUtterance()
    {
        var logger = new InMemoryDiagnosticLogger();
        var observer = new ShadowStabilityObserver(logger, "en-US->de-DE", 1, new PrefixStabilityEngine());

        observer.BeginUtterance("gen1-utt0", T(0));
        observer.ObservePartial(1, "a", T(10));
        observer.ObservePartial(2, "a b", T(20));
        observer.ObservePartial(3, "a b c", T(30));

        Assert.Single(logger.Entries, e => e.EventType == "ShadowFirstPartial");
        Assert.Single(logger.Entries, e => e.EventType == "ShadowFirstCommit");
    }
}
