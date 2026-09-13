using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Tests for the Step 5 shadow incremental-translation evaluation layer. No Azure
/// dependency — <see cref="AzureAlignedIncrementalTranslationShadow"/> operates purely on
/// synthetic "Azure-provided" translated text strings, exactly the shape the real
/// provider already produces on every Recognizing/Recognized event.
/// </summary>
public class AzureAlignedIncrementalTranslationShadowTests
{
    private const string U = "utt-0";

    private static IncrementalTranslationInput Partial(
        int seq, string? newlyStable, string cumulativeStable, string translated, string uttId = U) =>
        new(uttId, seq, "en-US", "de-DE", newlyStable, cumulativeStable, translated, IsFinal: false);

    private static IncrementalTranslationInput Final(
        int seq, string finalSource, string finalTranslated, string uttId = U) =>
        new(uttId, seq, "en-US", "de-DE", null, finalSource, finalTranslated, IsFinal: true,
            FinalSourceText: finalSource, FinalTranslatedText: finalTranslated);

    // ---- Single segment ----
    [Fact]
    public void SingleSegment_FirstPartial_ProducesNoCandidates_NothingToCompareAgainstYet()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        var results = shadow.ObservePartial(Partial(1, null, "", "Ich"));
        Assert.Empty(results); // Strategy A has no previous to diff against; B/C are not stability-gated this step
    }

    // ---- Growing segments ----
    [Fact]
    public void GrowingSegments_AllThreeStrategiesProduceCandidates_WhenGated()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);

        shadow.ObservePartial(Partial(1, null, "", "Ich"));
        var r2 = shadow.ObservePartial(Partial(2, "Ich", "Ich", "Ich möchte"));

        var strategies = r2.Select(x => x.StrategyName).ToHashSet();
        Assert.Contains("A_NaiveIndependent", strategies); // fires every partial with a delta
        Assert.Contains("B_CumulativeReTranslation", strategies); // gated, and this step IS gated
        Assert.Contains("C_GatedRollingWindow", strategies);
    }

    [Fact]
    public void GrowingSegments_StrategyB_AlwaysReplacesWholesale()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich"));
        var r2 = shadow.ObservePartial(Partial(2, "Ich", "Ich", "Ich möchte"));

        var b = r2.Single(x => x.StrategyName == "B_CumulativeReTranslation");
        Assert.Equal(IncrementalCandidateRelation.Replaces, b.RelationToPrevious);
        Assert.Equal("Ich möchte", b.CumulativeTranslatedText);
    }

    // ---- Repeated segments ----
    [Fact]
    public void RepeatedIdenticalTranslatedText_ProducesNoNewCandidate()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich möchte"));
        var r2 = shadow.ObservePartial(Partial(2, null, "", "Ich möchte")); // unchanged translated text, not gated

        Assert.Empty(r2); // no delta for A (identical), and not gated for B/C
    }

    // ---- Corrections (contradiction relative to previous translated text) ----
    [Fact]
    public void StrategyA_ContradictingTranslatedText_BlindlyAppendsAnyway_ReproducingTheNaiveFailureMode()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich")); // first partial: nothing to diff against yet
        shadow.ObservePartial(Partial(2, null, "", "Ich möchte Dienstag")); // "Dienstag" gets committed to cumulative here
        var r3 = shadow.ObservePartial(Partial(3, null, "", "Ich möchte Donnerstag")); // "Dienstag"->"Donnerstag": contradiction

        var a = r3.Single(x => x.StrategyName == "A_NaiveIndependent");
        Assert.Equal(IncrementalCandidateRelation.Revises, a.RelationToPrevious);
        // Blindly appended on top of the earlier (now-wrong) "Dienstag" — cumulative contains
        // BOTH words simultaneously, concretely demonstrating the naive-strategy failure mode.
        Assert.Contains("Dienstag", a.CumulativeTranslatedText);
        Assert.Contains("Donnerstag", a.CumulativeTranslatedText);
    }

    [Fact]
    public void StrategyC_ContradictingTranslatedText_WithheldNotAppended()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich möchte Dienstag"));
        var r2 = shadow.ObservePartial(Partial(2, "stable", "x", "Ich möchte Dienstag")); // gated, first ref set
        var r3 = shadow.ObservePartial(Partial(3, "stable2", "x y", "Ich möchte Donnerstag")); // gated, contradicts C's reference

        Assert.DoesNotContain(r3, x => x.StrategyName == "C_GatedRollingWindow"); // withheld, not appended
    }

    // ---- Final divergence ----
    [Fact]
    public void FinalReconciliation_MeasuresMissingAndDuplicateTokens_StructurallyNotSemantic()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich"));
        shadow.ObservePartial(Partial(2, null, "", "Ich möchte Dienstag")); // "Dienstag" committed
        shadow.ObservePartial(Partial(3, null, "", "Ich möchte Donnerstag")); // strategy A appends "Donnerstag" too, contradicting

        var reconciliations = shadow.ObserveFinal(Final(4, "final source", "Ich möchte Donnerstag treffen"));

        var a = reconciliations.Single(r => r.StrategyName == "A_NaiveIndependent");
        // A's cumulative contains the stray "Dienstag" the final doesn't — a duplicate/extra token.
        Assert.True(a.ApproxDuplicateTokenCount > 0);
    }

    // ---- Empty segments ----
    [Fact]
    public void EmptyTranslatedText_HandledWithoutCorruptingState()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich"));
        var r2 = shadow.ObservePartial(Partial(2, null, "", "")); // empty translated text this step
        var r3 = shadow.ObservePartial(Partial(3, null, "", "Ich möchte"));

        Assert.Empty(r2);
        Assert.NotEmpty(r3); // recovers normally afterward
    }

    // ---- Multiple utterances / context reset ----
    [Fact]
    public void NewUtteranceId_FullyResetsAllThreeStrategies()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich", "utt-a"));
        shadow.ObservePartial(Partial(2, "x", "x", "Ich möchte", "utt-a"));
        shadow.ObserveFinal(Final(3, "final", "Ich möchte etwas.", "utt-a"));

        // New utterance: first partial must behave like a true first partial for every strategy.
        var r = shadow.ObservePartial(Partial(1, null, "", "Hallo", "utt-b"));
        Assert.Empty(r); // no leftover state from utt-a causing a spurious delta
    }

    // ---- Duplicate prevention across a full utterance for Strategy C (the "smart" strategy) ----
    [Fact]
    public void StrategyC_NeverDuplicatesAcrossMonotonicGrowth()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        var segments = new List<string>();

        void Run(int seq, string? newlyStable, string translated)
        {
            var r = shadow.ObservePartial(Partial(seq, newlyStable, "x", translated));
            var c = r.FirstOrDefault(x => x.StrategyName == "C_GatedRollingWindow");
            if (c != null) segments.Add(c.CandidateTranslatedText);
        }

        Run(1, null, "Ich");
        Run(2, "s1", "Ich möchte");
        Run(3, "s2", "Ich möchte ein Treffen");
        Run(4, "s3", "Ich möchte ein Treffen vereinbaren");

        var reconstructed = string.Join(" ", segments);
        Assert.Equal("Ich möchte ein Treffen vereinbaren", reconstructed); // exactly once each
    }

    // ---- Context model: Strategy A ungated fires on every partial with a delta, B/C only when gated ----
    [Fact]
    public void UngatedPartial_OnlyStrategyAMayProduceACandidate()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich"));
        var r2 = shadow.ObservePartial(Partial(2, null, "", "Ich möchte")); // no newlyStable => not gated

        Assert.All(r2, x => Assert.Equal("A_NaiveIndependent", x.StrategyName));
        Assert.Single(r2);
    }

    // ---- Privacy: never logs recognized/translated text, only metadata ----
    [Fact]
    public void NeverLogsTranslatedOrSourceText_OnlyMetadata()
    {
        var logger = new InMemoryDiagnosticLogger();
        var shadow = new AzureAlignedIncrementalTranslationShadow(logger, "en-US->de-DE", 1);
        const string sensitiveTranslated = "Ich möchte ein vertrauliches Treffen vereinbaren";

        shadow.ObservePartial(Partial(1, null, "", sensitiveTranslated));
        shadow.ObservePartial(Partial(2, "x", "x", sensitiveTranslated + " morgen"));
        shadow.ObserveFinal(Final(3, "final source text", sensitiveTranslated + " morgen früh."));

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitiveTranslated) || e.Details.Contains("Treffen"));
        Assert.Contains(logger.Entries, e => e.EventType == "TranslationShadowPartial");
        Assert.Contains(logger.Entries, e => e.EventType == "TranslationShadowFinalReconciliation");
    }

    // ---- Final reconciliation happens for all three strategies, every utterance ----
    [Fact]
    public void ObserveFinal_AlwaysReturnsReconciliationForAllThreeStrategies()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich"));
        var results = shadow.ObserveFinal(Final(2, "final", "Ich möchte."));

        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.StrategyName == "A_NaiveIndependent");
        Assert.Contains(results, r => r.StrategyName == "B_CumulativeReTranslation");
        Assert.Contains(results, r => r.StrategyName == "C_GatedRollingWindow");
    }

    // ---- Out-of-order handling: this layer has no sequence-staleness concept of its own
    // (Policy A already rejects stale partials before NewlyStableSourceSegment would ever
    // be set for one) — verify a repeated sequence number with unchanged content is still
    // safely a no-op rather than corrupting cumulative state.
    [Fact]
    public void RepeatedSequenceNumber_UnchangedContent_NoDuplicateCandidate()
    {
        var shadow = new AzureAlignedIncrementalTranslationShadow(new InMemoryDiagnosticLogger(), "en-US->de-DE", 1);
        shadow.ObservePartial(Partial(1, null, "", "Ich"));
        shadow.ObservePartial(Partial(2, null, "", "Ich möchte"));
        var repeat = shadow.ObservePartial(Partial(2, null, "", "Ich möchte")); // same seq+content again

        Assert.Empty(repeat); // no new delta since translated text didn't change
    }
}
