using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Tests for Policy B (<see cref="BestPartialStabilityEngine"/>), Step 4's experimental,
/// shadow-only commit-policy candidate. Mirrors the structure of
/// <see cref="PrefixStabilityEngineTests"/> (Policy A) for the shared behaviors, and adds
/// dedicated tests for the one place the two policies diverge: which prior partial is
/// used as the comparison baseline.
/// </summary>
public class BestPartialStabilityEngineTests
{
    private const string U = "utterance-1";
    private static DateTimeOffset T(int i) => DateTimeOffset.UnixEpoch.AddSeconds(i);

    private static PartialSourceEvent P(int seq, string text, string uttId = U) => new(uttId, seq, text, T(seq));
    private static FinalSourceEvent F(int seq, string text, string uttId = U) => new(uttId, seq, text, T(seq));

    // ---- Monotonic growth: identical to Policy A's canonical example ----
    [Fact]
    public void MonotonicGrowth_CommitsIncrementally_MatchesFinalExactly()
    {
        var engine = new BestPartialStabilityEngine();
        var segments = new List<string>();

        void Run(int seq, string text)
        {
            var r = engine.ProcessPartial(P(seq, text));
            if (r.ShouldEmit) segments.Add(r.NewlyCommittedSegment!);
        }

        Run(1, "I would");
        Run(2, "I would like");
        Run(3, "I would like to");
        Run(4, "I would like to schedule");
        Run(5, "I would like to schedule a meeting");

        var final = engine.ProcessFinal(F(6, "I would like to schedule a meeting."));
        if (final.ShouldEmit) segments.Add(final.NewlyCommittedSegment!);

        Assert.Equal("I would like to schedule a meeting.", string.Join(" ", segments));
        Assert.False(final.FinalizedWithCorrection);
    }

    // ---- Repeated partials (identical text, e.g. whitespace-only difference) ----
    [Fact]
    public void RepeatedPartial_DoesNotDuplicateOrCorrupt_AndStillRecoversFullyAtFinal()
    {
        var engine = new BestPartialStabilityEngine();

        var r1 = engine.ProcessPartial(P(1, "I would like"));
        Assert.False(r1.ShouldEmit);
        var r2 = engine.ProcessPartial(P(2, "I   would    like")); // whitespace-only repeat
        Assert.True(r2.ShouldEmit);
        Assert.Equal("I would", r2.NewlyCommittedSegment);

        var final = engine.ProcessFinal(F(3, "I would like."));
        var reconstructed = string.Join(" ", new[] { r2.NewlyCommittedSegment, final.NewlyCommittedSegment }.Where(s => s != null));
        Assert.Equal("I would like.", reconstructed);
        Assert.False(final.FinalizedWithCorrection);
    }

    // ---- Punctuation changes: same comparison-only normalization as Policy A's V-1 ----
    [Fact]
    public void TrailingPunctuationOnCommittedWord_NotTreatedAsCorrection()
    {
        var engine = new BestPartialStabilityEngine();
        engine.ProcessPartial(P(1, "a meeting"));
        var p2 = engine.ProcessPartial(P(2, "a meeting today"));
        Assert.Equal("a", p2.NewlyCommittedSegment);
        var p3 = engine.ProcessPartial(P(3, "a meeting today now"));
        Assert.Equal("meeting", p3.NewlyCommittedSegment);

        var final = engine.ProcessFinal(F(4, "a meeting."));
        Assert.False(final.FinalizedWithCorrection);
        Assert.False(final.ShouldEmit); // "meeting." vs committed "meeting" is not new content
    }

    // ---- Source regression / recovery: THE key Policy A vs Policy B divergence ----
    // A long, good partial is followed by a shorter partial (Azure can genuinely emit a
    // shorter revision), then a corrected, full-length partial. Policy A would compare the
    // corrected partial against the SHORT intervening partial (its literal "previous"),
    // capping the achievable agreement and stalling the commit. Policy B keeps the longer,
    // still-consistent partial as its reference, so the corrected partial's real agreement
    // is not artificially capped.
    [Fact]
    public void RegressionThenRecovery_ReferenceIsNotDegradedByAShorterIntermediatePartial()
    {
        var engine = new BestPartialStabilityEngine();

        engine.ProcessPartial(P(1, "I want to meet on Tuesday next week")); // long, good — becomes reference
        var r2 = engine.ProcessPartial(P(2, "I want to")); // shorter — must NOT become the new reference
        Assert.True(r2.ShouldEmit);
        Assert.Equal("I want", r2.NewlyCommittedSegment);
        Assert.Equal("I want", r2.CommittedSourceText);

        var r3 = engine.ProcessPartial(P(3, "I want to meet on Thursday next week")); // corrected, full-length again
        // Because the reference is still the 8-token partial 1 (not the 3-token partial 2),
        // agreement against it reaches "I want to meet on" (5 tokens, differing at "Thursday"),
        // committable = 4, which exceeds the already-committed 2 — so THIS step commits
        // "to meet" immediately, rather than stalling (as Policy A does for this exact
        // sequence — see docs/design-notes/commit-policy-shadow-evaluation.md §10 for the
        // side-by-side trace).
        Assert.True(r3.ShouldEmit);
        Assert.Equal("to meet", r3.NewlyCommittedSegment);
        Assert.Equal("I want to meet", r3.CommittedSourceText);
    }

    // ---- Regression that is a genuine contradiction (not just shorter) — must still be withheld ----
    [Fact]
    public void ContradictingLongerPartial_DoesNotBecomeReference_AndIsNotCommitted()
    {
        var engine = new BestPartialStabilityEngine();
        engine.ProcessPartial(P(1, "I want to book"));
        var r2 = engine.ProcessPartial(P(2, "I want to book a"));
        Assert.Equal("I want to", r2.NewlyCommittedSegment);

        // Longer than the current reference, but contradicts already-committed content —
        // must NOT be adopted as the new reference, and must NOT be committed.
        var r3 = engine.ProcessPartial(P(3, "I'd like to book a table now")); // 6 tokens > reference's 5
        Assert.Equal(StabilityAction.None, r3.Action);
        Assert.Equal("I want to", r3.CommittedSourceText); // unchanged, not retracted

        // A subsequent partial that agrees with the ORIGINAL reference should still commit
        // normally — proving the contradicting partial never replaced the reference.
        var r4 = engine.ProcessPartial(P(4, "I want to book a table"));
        Assert.True(r4.ShouldEmit);
    }

    // ---- Self-correction on the trailing (held-back) word: same safety as Policy A ----
    [Fact]
    public void SelfCorrection_TuesdayToThursday_NeverFalselyCommitted()
    {
        var engine = new BestPartialStabilityEngine();
        engine.ProcessPartial(P(1, "I want to meet on Tuesday"));
        var r2 = engine.ProcessPartial(P(2, "I want to meet on Thursday"));
        Assert.Equal("I want to meet", r2.NewlyCommittedSegment); // "on" held back as boundary word
        Assert.DoesNotContain("Tuesday", r2.CommittedSourceText);
        Assert.DoesNotContain("Thursday", r2.CommittedSourceText);
    }

    // ---- The exact adversarial example from the Step 4 prompt ----
    [Fact]
    public void AdversarialExample_TuesdayThenThursdayThenIdLike_NeverFalselyCommitsAndFinalResolvesCorrectly()
    {
        var engine = new BestPartialStabilityEngine();

        engine.ProcessPartial(P(1, "I want to meet on Tuesday"));
        var r2 = engine.ProcessPartial(P(2, "I want to meet on Thursday"));
        Assert.Equal("I want to meet", r2.NewlyCommittedSegment);

        // "I'd" contradicts the committed "I" — must not be committed, must not replace the
        // reference, must not throw or corrupt state.
        var r3 = engine.ProcessPartial(P(3, "I'd like to meet on Thursday"));
        Assert.Equal(StabilityAction.None, r3.Action);
        Assert.Equal("I want to meet", r3.CommittedSourceText);

        var final = engine.ProcessFinal(F(4, "I'd like to meet on Thursday."));
        Assert.True(final.FinalizedWithCorrection);
        Assert.Equal("I'd like to meet on Thursday.", final.CommittedSourceText);
        // No duplication: the incorrect "I want to meet" prefix is not repeated inside the
        // Final's own emitted text (CommittedSourceText is always the literal final text).
        Assert.DoesNotContain("want", final.CommittedSourceText);
    }

    // ---- Final result divergence: Final is always authoritative regardless of policy ----
    [Fact]
    public void FinalWithMoreContentThanAnyPartialSaw_FullyEmitted()
    {
        var engine = new BestPartialStabilityEngine();
        engine.ProcessPartial(P(1, "I would"));
        var r2 = engine.ProcessPartial(P(2, "I would like"));
        Assert.Equal("I", r2.NewlyCommittedSegment);

        var final = engine.ProcessFinal(F(3, "I would like to schedule a meeting tomorrow."));
        Assert.Equal("I would like to schedule a meeting tomorrow.", final.CommittedSourceText);
        Assert.True(final.ShouldEmit);
    }

    // ---- Multiple utterances: full isolation, including the reference field ----
    [Fact]
    public void MultipleUtterances_ReferenceAndCommittedStateFullyIsolated()
    {
        var engine = new BestPartialStabilityEngine();

        engine.ProcessPartial(P(1, "hello there", "utt-a"));
        var a2 = engine.ProcessPartial(P(2, "hello there friend", "utt-a"));
        Assert.Equal("hello", a2.NewlyCommittedSegment);
        engine.ProcessFinal(F(3, "hello there friend.", "utt-a"));

        // New utterance: first partial must behave like a true first partial (no commit),
        // proving the long "hello there friend" reference from utt-a did not leak over.
        var b1 = engine.ProcessPartial(P(1, "goodbye", "utt-b"));
        Assert.False(b1.ShouldEmit);
        Assert.Equal("", b1.CommittedSourceText);
    }

    // ---- Out-of-order / stale partial rejection: same as Policy A ----
    [Fact]
    public void StaleOutOfOrderPartial_Rejected_NoStateChange()
    {
        var engine = new BestPartialStabilityEngine();
        engine.ProcessPartial(P(3, "I would like to"));
        var stale = engine.ProcessPartial(P(2, "I would")); // sequence went backwards
        Assert.Equal(StabilityAction.None, stale.Action);
    }

    // ---- Determinism ----
    [Fact]
    public void SameInputSequence_ProducesIdenticalResults_RegardlessOfTimestamps()
    {
        List<string?> Run(Func<int, DateTimeOffset> ts)
        {
            var engine = new BestPartialStabilityEngine();
            var results = new List<string?>();
            results.Add(engine.ProcessPartial(new PartialSourceEvent(U, 1, "I would", ts(1))).NewlyCommittedSegment);
            results.Add(engine.ProcessPartial(new PartialSourceEvent(U, 2, "I would like", ts(2))).NewlyCommittedSegment);
            results.Add(engine.ProcessPartial(new PartialSourceEvent(U, 3, "I would like to schedule", ts(3))).NewlyCommittedSegment);
            return results;
        }

        var a = Run(i => T(i));
        var b = Run(i => T(i * 1000));

        Assert.Equal(a, b);
    }
}
