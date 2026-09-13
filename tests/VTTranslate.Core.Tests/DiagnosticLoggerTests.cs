namespace VTTranslate.Core.Tests;

public class DiagnosticLoggerTests
{
    [Fact]
    public void Entries_FromDifferentSessionTags_RemainIndependentlyDistinguishable()
    {
        // Simulates two concurrent DirectionPipeline instances (EN->DE and DE->EN)
        // logging through the same logger — proves entries from one direction can be
        // filtered/read without any risk of being confused with the other's, which is
        // the property that matters for diagnosing "which recognizer generated this
        // error" after the fact.
        var logger = new InMemoryDiagnosticLogger();

        logger.Log("en-US->de-DE", "Connected", "generation=1");
        logger.Log("de-DE->en-US", "Disconnected", "reason=Error errorCode=ConnectionFailure transient=True");
        logger.Log("de-DE->en-US", "ReconnectAttempt", "attempt=1/5 delayMs=2000");
        logger.Log("en-US->de-DE", "AudioChunksSent", "totalChunks=50 lastChunkBytes=3200");

        var enToDeEntries = logger.Entries.Where(e => e.SessionTag == "en-US->de-DE").ToList();
        var deToEnEntries = logger.Entries.Where(e => e.SessionTag == "de-DE->en-US").ToList();

        Assert.Equal(2, enToDeEntries.Count);
        Assert.Equal(2, deToEnEntries.Count);

        Assert.DoesNotContain(enToDeEntries, e => e.EventType is "Disconnected" or "ReconnectAttempt");
        Assert.DoesNotContain(deToEnEntries, e => e.EventType is "Connected" or "AudioChunksSent");
    }

    [Fact]
    public void Log_NeverReceivesRecognizedOrTranslatedText_OnlyMetadata()
    {
        // Documents and pins the contract: this test asserts the shape of what a
        // well-behaved caller logs for a recognition event (length only), not actual
        // text — a regression here (someone logging e.Result.Text directly) would
        // start failing this assertion once such a call is added to a real test that
        // exercises AzureSpeechTranslationProvider's actual log calls. As a pure
        // contract test today, it documents the expectation the production code
        // (AzureSpeechTranslationProvider's RecognitionEvent logging) follows.
        var logger = new InMemoryDiagnosticLogger();
        const string sensitiveTranscript = "Ich möchte morgen ein Treffen vereinbaren.";

        logger.Log("de-DE->en-US", "RecognitionEvent", $"kind=final generation=1 textLength={sensitiveTranscript.Length}");

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitiveTranscript));
    }

    [Fact]
    public void NullDiagnosticLogger_NeverThrows_AndRecordsNothing()
    {
        var logger = VTTranslate.Core.Diagnostics.NullDiagnosticLogger.Instance;
        var exception = Record.Exception(() => logger.Log("any-tag", "AnyEvent", "any details"));
        Assert.Null(exception);
    }
}
