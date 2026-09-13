namespace VTTranslate.Core.Streaming;

public enum StabilityAction { None, Committed, Finalized }

/// <summary>
/// One Azure <c>Recognizing</c> (partial) event, reduced to what
/// <see cref="IStreamingStabilityEngine"/> needs. <see cref="TranslatedTextForDiagnosticsOnly"/>
/// is exactly that — diagnostics only; see PrefixStabilityEngine's doc comment for why
/// translated text is never used for stability decisions.
/// </summary>
public sealed record PartialSourceEvent(
    string UtteranceId,
    int SequenceNumber,
    string SourceText,
    DateTimeOffset Timestamp,
    string? TranslatedTextForDiagnosticsOnly = null);

/// <summary>One Azure <c>Recognized</c> (final) event, reduced to what the engine needs.</summary>
public sealed record FinalSourceEvent(
    string UtteranceId,
    int SequenceNumber,
    string SourceText,
    DateTimeOffset Timestamp);

/// <summary>
/// Result of processing one partial or final event.
/// </summary>
/// <param name="Action">What just happened: nothing new, a normal commit, or utterance finalization.</param>
/// <param name="CommittedSourceText">The engine's full current best understanding of the stable/confirmed text so far (space-joined tokens).</param>
/// <param name="NewlyCommittedSegment">Only the newly-committed words from THIS event, or null if nothing new was committed. This is what a future downstream consumer would translate/synthesize incrementally.</param>
/// <param name="PendingUnstableText">The remainder of the latest partial beyond what's committed — not yet safe to act on.</param>
/// <param name="CommitVersion">Increments each time <see cref="NewlyCommittedSegment"/> is non-null; resets to 0 for each new utterance.</param>
/// <param name="ShouldEmit">Convenience: true iff <see cref="NewlyCommittedSegment"/> is non-null.</param>
/// <param name="FinalizedWithCorrection">
/// True only on a <see cref="StabilityAction.Finalized"/> result where the final text
/// contradicted something already committed — see docs/design-notes/prefix-stability-engine.md
/// "Regression handling" for what this means and its accepted limitation.
/// </param>
public sealed record StabilityResult(
    StabilityAction Action,
    string CommittedSourceText,
    string? NewlyCommittedSegment,
    string PendingUnstableText,
    int CommitVersion,
    bool ShouldEmit,
    bool FinalizedWithCorrection = false);
