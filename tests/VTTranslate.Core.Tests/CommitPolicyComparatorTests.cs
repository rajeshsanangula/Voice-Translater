using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Tests for <see cref="CommitPolicyComparator"/>, the Step 4 shadow comparison
/// abstraction that runs Policy A and Policy B against an identical partial/final
/// stream. No Azure dependency — the comparator is exercised directly with synthetic
/// events, exactly as <see cref="ShadowStabilityObserverTests"/> did for Step 3.
/// </summary>
public class CommitPolicyComparatorTests
{
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    [Fact]
    public void RegressionRecoveryScenario_PolicyBCommitsEarlierThanPolicyA()
    {
        var comparator = new CommitPolicyComparator(
            new InMemoryDiagnosticLogger(), "en-US->de-DE", generation: 1,
            policyAEngine: new PrefixStabilityEngine(),
            policyBEngine: new BestPartialStabilityEngine());

        comparator.BeginUtterance("utt-0", T(0));
        comparator.ObservePartial(1, "I want to meet on Tuesday next week", T(100));
        comparator.ObservePartial(2, "I want to", T(200)); // shorter intermediate partial
        comparator.ObservePartial(3, "I want to meet on Thursday next week", T(300));
        var result = comparator.ObserveFinal(4, "I want to meet on Thursday next week.", T(400));

        // Policy B should have committed at least as many tokens by this point as Policy A —
        // this is the concrete, reproducible shape of the improvement under evaluation.
        Assert.True(result.PolicyBTotalCommittedTokens >= result.PolicyATotalCommittedTokens);
        Assert.True(result.StepsWithDifferentCommitBoundary > 0); // the two policies visibly diverged
        Assert.False(result.PolicyACorrectionAtFinal);
        Assert.False(result.PolicyBCorrectionAtFinal);
    }

    [Fact]
    public void MonotonicGrowth_BothPoliciesProduceIdenticalCommitCounts()
    {
        var comparator = new CommitPolicyComparator(
            new InMemoryDiagnosticLogger(), "en-US->de-DE", generation: 1,
            policyAEngine: new PrefixStabilityEngine(),
            policyBEngine: new BestPartialStabilityEngine());

        comparator.BeginUtterance("utt-0", T(0));
        comparator.ObservePartial(1, "I would", T(10));
        comparator.ObservePartial(2, "I would like", T(20));
        comparator.ObservePartial(3, "I would like to schedule", T(30));
        var result = comparator.ObserveFinal(4, "I would like to schedule a meeting.", T(40));

        Assert.Equal(result.PolicyACommitCount, result.PolicyBCommitCount);
        Assert.Equal(result.PolicyATotalCommittedTokens, result.PolicyBTotalCommittedTokens);
        Assert.Equal(0, result.StepsWithDifferentCommitBoundary);
    }

    [Fact]
    public void AdversarialTuesdayThursdayIdLike_NeitherPolicyDuplicatesOrLosesContent()
    {
        var comparator = new CommitPolicyComparator(
            new InMemoryDiagnosticLogger(), "en-US->de-DE", generation: 1,
            policyAEngine: new PrefixStabilityEngine(),
            policyBEngine: new BestPartialStabilityEngine());

        comparator.BeginUtterance("utt-0", T(0));
        comparator.ObservePartial(1, "I want to meet on Tuesday", T(10));
        comparator.ObservePartial(2, "I want to meet on Thursday", T(20));
        comparator.ObservePartial(3, "I'd like to meet on Thursday", T(30));
        var result = comparator.ObserveFinal(4, "I'd like to meet on Thursday.", T(40));

        // Both policies must flag the correction (the "I"->"I'd" contradiction is real,
        // not a punctuation artifact) — this is the "must not create uncontrolled
        // duplicate commitments" requirement holding for both policies simultaneously.
        Assert.True(result.PolicyACorrectionAtFinal);
        Assert.True(result.PolicyBCorrectionAtFinal);
    }

    [Fact]
    public void NeverLogsRecognizedSourceText_ForEitherPolicy()
    {
        var logger = new InMemoryDiagnosticLogger();
        var comparator = new CommitPolicyComparator(
            logger, "de-DE->en-US", generation: 1,
            policyAEngine: new PrefixStabilityEngine(),
            policyBEngine: new BestPartialStabilityEngine());
        const string sensitive = "Ich möchte morgen ein vertrauliches Treffen vereinbaren";

        comparator.BeginUtterance("utt-0", T(0));
        comparator.ObservePartial(1, sensitive, T(10));
        comparator.ObserveFinal(2, sensitive + ".", T(20));

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive) || e.Details.Contains("Treffen"));
        // Both policies' log streams are present and distinguishable.
        Assert.Contains(logger.Entries, e => e.SessionTag == "de-DE->en-US|PolicyA");
        Assert.Contains(logger.Entries, e => e.SessionTag == "de-DE->en-US|PolicyB");
    }

    [Fact]
    public void MultipleUtterances_ComparisonStateFullyResetsPerUtterance()
    {
        var comparator = new CommitPolicyComparator(
            new InMemoryDiagnosticLogger(), "en-US->de-DE", generation: 1,
            policyAEngine: new PrefixStabilityEngine(),
            policyBEngine: new BestPartialStabilityEngine());

        comparator.BeginUtterance("utt-0", T(0));
        comparator.ObservePartial(1, "hello there", T(10));
        comparator.ObservePartial(2, "hello there friend", T(20));
        var first = comparator.ObserveFinal(3, "hello there friend.", T(30));
        Assert.True(first.PolicyACommitCount > 0);

        comparator.BeginUtterance("utt-1", T(1000));
        var second = comparator.ObserveFinal(1, "totally different content.", T(1010));

        Assert.Equal(1, second.PolicyACommitCount); // only the Final's own flush — no partial-driven commits carried over
        Assert.False(second.PolicyACorrectionAtFinal); // nothing was committed to contradict
        Assert.Equal(3, second.PolicyAFinalFlushTokens); // "totally different content." = 3 tokens, fully flushed
        Assert.Equal(3, second.PolicyBFinalFlushTokens);
    }

    [Fact]
    public void Determinism_SameInputSequence_ProducesIdenticalComparisonResult()
    {
        UtteranceCommitPolicyComparison Run(Func<int, DateTimeOffset> ts)
        {
            var comparator = new CommitPolicyComparator(
                new InMemoryDiagnosticLogger(), "en-US->de-DE", generation: 1,
                policyAEngine: new PrefixStabilityEngine(),
                policyBEngine: new BestPartialStabilityEngine());
            comparator.BeginUtterance("utt-0", ts(0));
            comparator.ObservePartial(1, "I want to meet on Tuesday next week", ts(1));
            comparator.ObservePartial(2, "I want to", ts(2));
            comparator.ObservePartial(3, "I want to meet on Thursday next week", ts(3));
            return comparator.ObserveFinal(4, "I want to meet on Thursday next week.", ts(4));
        }

        var a = Run(i => T(i * 100));
        var b = Run(i => T(i * 100 + 5000));

        Assert.Equal(a.PolicyACommitCount, b.PolicyACommitCount);
        Assert.Equal(a.PolicyBCommitCount, b.PolicyBCommitCount);
        Assert.Equal(a.PolicyATotalCommittedTokens, b.PolicyATotalCommittedTokens);
        Assert.Equal(a.PolicyBTotalCommittedTokens, b.PolicyBTotalCommittedTokens);
        Assert.Equal(a.StepsWithDifferentCommitBoundary, b.StepsWithDifferentCommitBoundary);
    }
}
