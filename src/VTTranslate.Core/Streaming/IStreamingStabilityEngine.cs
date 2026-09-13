namespace VTTranslate.Core.Streaming;

/// <summary>
/// Consumes a stream of Azure partial/final recognition events for ONE translation
/// direction and determines which portion of the SOURCE speech is stable enough to
/// commit — i.e. safe to translate/synthesize incrementally in a future streaming
/// pipeline. Fully offline: no Azure/network dependency, deterministic, stateful only
/// in memory. Not connected to production translation/TTS as of Step 2 — see
/// docs/design-notes/prefix-stability-engine.md.
/// </summary>
public interface IStreamingStabilityEngine
{
    /// <summary>Process one Recognizing (partial) event.</summary>
    StabilityResult ProcessPartial(PartialSourceEvent partial);

    /// <summary>
    /// Process the Recognized (final) event for the current utterance — always
    /// authoritative; forces completion of any remaining unstable text and resets the
    /// engine for the next utterance.
    /// </summary>
    StabilityResult ProcessFinal(FinalSourceEvent final);

    /// <summary>Explicitly reset to initial state (also happens automatically after ProcessFinal, or on an utterance-ID change).</summary>
    void Reset();
}
