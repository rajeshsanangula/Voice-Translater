using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 4. Runs Policy A (<see cref="PrefixStabilityEngine"/>,
/// unmodified) and Policy B (<see cref="BestPartialStabilityEngine"/>, experimental)
/// against the IDENTICAL partial/final source-text stream via two independent
/// <see cref="ShadowStabilityObserver"/> instances (reused unmodified from Step 3), and
/// accumulates a per-utterance comparison. Like <see cref="ShadowStabilityObserver"/>,
/// this class has no events and no reference to translation/TTS/playback — neither
/// policy's output can reach production audio through this class, structurally, not
/// merely by omission. See docs/design-notes/commit-policy-shadow-evaluation.md.
/// </summary>
public sealed class CommitPolicyComparator
{
    private readonly ShadowStabilityObserver _policyA;
    private readonly ShadowStabilityObserver _policyB;

    private string _utteranceId = "";
    private int _policyACommitCount;
    private int _policyBCommitCount;
    private int _policyATotalCommittedTokens;
    private int _policyBTotalCommittedTokens;
    private double? _policyAFirstCommitElapsedMs;
    private double? _policyBFirstCommitElapsedMs;
    private int _stepsWithDifferentCommitBoundary;
    private int _policyADelayedCommitSteps; // A committed later than B at a step where both eventually committed by that step
    private int _policyBDelayedCommitSteps;
    private DateTimeOffset _t0;

    public CommitPolicyComparator(
        IDiagnosticLogger logger,
        string sessionTag,
        int generation,
        IStreamingStabilityEngine policyAEngine,
        IStreamingStabilityEngine policyBEngine)
    {
        _policyA = new ShadowStabilityObserver(logger, $"{sessionTag}|PolicyA", generation, policyAEngine);
        _policyB = new ShadowStabilityObserver(logger, $"{sessionTag}|PolicyB", generation, policyBEngine);
    }

    public void BeginUtterance(string utteranceId, DateTimeOffset t0)
    {
        _utteranceId = utteranceId;
        _policyACommitCount = 0;
        _policyBCommitCount = 0;
        _policyATotalCommittedTokens = 0;
        _policyBTotalCommittedTokens = 0;
        _policyAFirstCommitElapsedMs = null;
        _policyBFirstCommitElapsedMs = null;
        _stepsWithDifferentCommitBoundary = 0;
        _policyADelayedCommitSteps = 0;
        _policyBDelayedCommitSteps = 0;
        _t0 = t0;

        _policyA.BeginUtterance(utteranceId, t0);
        _policyB.BeginUtterance(utteranceId, t0);
    }

    /// <summary>
    /// Observes one partial through both policies. Returns each policy's raw
    /// <see cref="StabilityResult"/> — used by Step 5's translation shadow layer to read
    /// Policy A's source-stability decision; production callers may discard it exactly as
    /// they discarded the previous <c>void</c> return, this is purely additive.
    /// </summary>
    public (StabilityResult PolicyA, StabilityResult PolicyB) ObservePartial(int sequenceNumber, string sourceText, DateTimeOffset timestamp)
    {
        var a = _policyA.ObservePartial(sequenceNumber, sourceText, timestamp);
        var b = _policyB.ObservePartial(sequenceNumber, sourceText, timestamp);
        RecordStep(a, b, timestamp, isFinal: false);
        return (a, b);
    }

    /// <summary>Finalizes both policies for this utterance and returns the full comparison. Diagnostic only.</summary>
    public UtteranceCommitPolicyComparison ObserveFinal(int sequenceNumber, string sourceText, DateTimeOffset timestamp)
    {
        var a = _policyA.ObserveFinal(sequenceNumber, sourceText, timestamp);
        var b = _policyB.ObserveFinal(sequenceNumber, sourceText, timestamp);
        RecordStep(a, b, timestamp, isFinal: true);

        return new UtteranceCommitPolicyComparison(
            UtteranceId: _utteranceId,
            PolicyACommitCount: _policyACommitCount,
            PolicyBCommitCount: _policyBCommitCount,
            PolicyAFirstCommitElapsedMs: _policyAFirstCommitElapsedMs,
            PolicyBFirstCommitElapsedMs: _policyBFirstCommitElapsedMs,
            PolicyATotalCommittedTokens: _policyATotalCommittedTokens,
            PolicyBTotalCommittedTokens: _policyBTotalCommittedTokens,
            PolicyAFinalFlushTokens: CountTokens(a.NewlyCommittedSegment),
            PolicyBFinalFlushTokens: CountTokens(b.NewlyCommittedSegment),
            PolicyACorrectionAtFinal: a.FinalizedWithCorrection,
            PolicyBCorrectionAtFinal: b.FinalizedWithCorrection,
            StepsWithDifferentCommitBoundary: _stepsWithDifferentCommitBoundary,
            PolicyADelayedCommitSteps: _policyADelayedCommitSteps,
            PolicyBDelayedCommitSteps: _policyBDelayedCommitSteps);
    }

    private void RecordStep(StabilityResult a, StabilityResult b, DateTimeOffset timestamp, bool isFinal)
    {
        var aTokens = CountTokens(a.NewlyCommittedSegment);
        var bTokens = CountTokens(b.NewlyCommittedSegment);

        var elapsedMs = (timestamp - _t0).TotalMilliseconds;

        if (a.ShouldEmit)
        {
            _policyACommitCount++;
            _policyATotalCommittedTokens += aTokens;
            if (!isFinal) _policyAFirstCommitElapsedMs ??= elapsedMs;
        }
        if (b.ShouldEmit)
        {
            _policyBCommitCount++;
            _policyBTotalCommittedTokens += bTokens;
            if (!isFinal) _policyBFirstCommitElapsedMs ??= elapsedMs;
        }

        // "Boundary differs" this step: one policy committed and the other didn't, or both
        // committed but a different number of tokens — a structural signal (never text
        // content) that the two policies reached a different stability decision at this point.
        if (a.ShouldEmit != b.ShouldEmit || aTokens != bTokens)
            _stepsWithDifferentCommitBoundary++;

        // "Delayed" relative to the other policy at this same step: the other committed
        // here and this one didn't yet (a coarse, per-step signal — not a claim about which
        // policy is "right", just which one moved first at this point in the stream).
        if (b.ShouldEmit && !a.ShouldEmit) _policyADelayedCommitSteps++;
        if (a.ShouldEmit && !b.ShouldEmit) _policyBDelayedCommitSteps++;
    }

    private static int CountTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>
/// Per-utterance Policy A vs. Policy B comparison. Every field is a count/boolean/elapsed-ms
/// value — never recognized/translated text — consistent with this project's privacy rule.
/// </summary>
public sealed record UtteranceCommitPolicyComparison(
    string UtteranceId,
    int PolicyACommitCount,
    int PolicyBCommitCount,
    double? PolicyAFirstCommitElapsedMs,
    double? PolicyBFirstCommitElapsedMs,
    int PolicyATotalCommittedTokens,
    int PolicyBTotalCommittedTokens,
    int PolicyAFinalFlushTokens,
    int PolicyBFinalFlushTokens,
    bool PolicyACorrectionAtFinal,
    bool PolicyBCorrectionAtFinal,
    int StepsWithDifferentCommitBoundary,
    int PolicyADelayedCommitSteps,
    int PolicyBDelayedCommitSteps);
