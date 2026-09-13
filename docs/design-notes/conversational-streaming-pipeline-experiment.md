# Step 5.13 — Controlled Conversational Streaming Pipeline Experiment

**Status: COMPLETE. Experimental, shadow-only, fully isolated from production. Not integrated into production. Do not start Step 5.14 without explicit authorization.**

## Legend

- **PROVEN** — demonstrated by a passing automated test (unit test or this document's own live-Azure run) with the actual output shown.
- **OBSERVED** — a real measurement was taken (via `dotnet test` or the `conversational-streaming-pipeline-test` live harness), not fabricated, but from a limited/synthetic scenario, not a full production acoustic pipeline.
- **UNVALIDATED** — not yet tested in this environment (e.g., blocked, or simply out of this step's scope).
- **ASSUMED** — a deliberate design choice made without direct evidence, called out explicitly as such.

## 0. What this step reused vs. built new

Reused, **unmodified**, by composition only:

| Component | Source step | File |
|---|---|---|
| `PrefixStabilityEngine` (SOURCE stability) | Step 2 | `src/VTTranslate.Core/Streaming/PrefixStabilityEngine.cs` |
| `SemanticCompletionHeuristic` (segment-boundary judgment) | Step 5.8 | `src/VTTranslate.Core/Streaming/SemanticCompletionHeuristic.cs` |
| C1/C2 bounded-context **concept** (not the Step 5.11 class itself — see §2) | Step 5.11 | — |
| `IIncrementalTranslationProvider` / `AzureTranslatorTextProvider` | Step 5.5/5.6a | `src/VTTranslate.Core/Streaming/IncrementalTranslationProviderModels.cs`, `AzureTranslatorTextProvider.cs` |
| `IStreamingTtsProvider` / `AzureStreamingTtsProvider` / `ITestPlaybackSink` (**frozen**, Step 5.12) | Step 5.12 | `src/VTTranslate.Core/Streaming/StreamingTtsModels.cs`, `AzureStreamingTtsProvider.cs` |

Built new for this step:

- `src/VTTranslate.Core/Streaming/ConversationalPipelineModels.cs` — request/result/state types.
- `src/VTTranslate.Core/Streaming/ConversationalStreamingPipelineExperiment.cs` — the orchestrator.
- `tests/VTTranslate.Core.Tests/ConversationalStreamingPipelineExperimentTests.cs` — 26 unit tests.
- `tools/VTTranslate.LiveTest/Program.cs`'s `conversational-streaming-pipeline-test` command.

**Confirmed untouched**: `grep -c "ConversationalStreamingPipeline\|PipelineSegment" src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs src/VTTranslate.App/*.cs` → `0` in every file (PROVEN). No reference to production playback, microphone capture, Google Meet, or Android/iOS anywhere in the new files (PROVEN by inspection — the new files have no `using` for any production audio/UI namespace).

`AzureStreamingTtsProvider.cs` and `StreamingTtsPipelineExperiment.cs` (Step 5.12, frozen) were **not modified** — confirmed via `git status` showing no changes to those two files during this step. Step 5.12's TTS findings remain **UNVALIDATED**; nothing in this step changes that status. This step's own live TTS calls (§8) are **new, Step-5.13-scoped OBSERVED evidence**, not a retroactive validation of Step 5.12.

## 1. Partial ASR → stable semantic segment

**Design**: A segment boundary is `PrefixStabilityEngine`'s `CommittedSourceText` (unmodified, source-stability-only signal) at the moment `SemanticCompletionHeuristic.Evaluate(...)` judges it complete — i.e., the trusted intersection of two independently-validated, previously-built signals. The pipeline emits only the **token delta since the previous boundary**, not the raw stability-commit delta (which can be smaller than a full semantic unit).

**Key finding, OBSERVED**: `PrefixStabilityEngine`'s one-word holdback means a sentence's LAST word is only committed on the *second* partial after it appears (the holdback word must be confirmed by the next partial's agreement). A naive "one partial per word, immediately mature" test assumption was WRONG — verified by hand-tracing (see `RealisticWordByWordStream_EventuallyProducesASentenceBoundary` and the `FeedWordByWord` test helper, which grows the partial text one real word at a time, exactly as Azure's `Recognizing` events do) and confirmed against the live run below (§8): a 2-sentence, 10-word utterance needed partial #4 and partial #9 (out of 10 total partials) to reach its two boundaries — not partials #2/#6 as a naive per-sentence-end assumption would predict. This is an inherited, not new, characteristic of the frozen `PrefixStabilityEngine` (documented originally in `prefix-stability-engine.md`), now additionally confirmed to compound with `SemanticCompletionHeuristic`'s own conservative bias.

**Speech pattern coverage** (PROVEN via unit tests, deterministic synthetic partials):
- Normal (`NormalSpeech_PunctuatedSentence_ProducesOneSegment`)
- Fast (`FastSpeech_ManyShortUtterancesInQuickSuccession_AllDelivered_FinalsNeverDropped`, `FastSpeech_ExceedingMaxQueueDepth_DropsOldestNonFinalSegments`)
- Long/multi-sentence (`LongUtterance_MultipleSentences_ProducesMultipleSegmentsFromARealisticPartialStream`)
- Short (`ShortUtterance_GoesStraightToFinalWithNoPartials`)
- Incomplete (`IncompleteUtterance_NeverReachesSemanticCompletion_NoSegmentEverQueued`)
- Self-correcting (`SelfCorrectingSpeech_ContradictingLaterPartial_DoesNotRetractAlreadyCommittedSegment`)
- Interrupted (`InterruptedSpeech_BargeInMidUtterance_OnlyNewUtteranceReachesSink`)

## 2. Bounded-context translation (C1/C2)

**ASSUMED, explicit deviation from Step 5.11**: Step 5.11's `ContextWindowTranslationExperiment` class itself was built to compare five strategies per commit step within a single utterance's test harness — its shape does not fit "drive one strategy through a live, multi-utterance pipeline." This step therefore **reimplements only the C1/C2 concept** (context = the source text of the preceding 1 or 2 committed segments, concatenated) against its own `_segmentHistory` list, rather than reusing the class. C3/C4 remain explicitly out of scope (constructor throws `ArgumentException` for anything but C0/C1/C2 — PROVEN by `OnlyC0C1C2Accepted_C3AndC4RejectedByConstructor`).

**ASSUMED extension**: unlike Step 5.11 (context scoped within one utterance's own steps), this step's `_segmentHistory` persists **across utterance boundaries** within the same session — a deliberate choice made specifically to exercise concern 10 (consecutive utterances). PROVEN by `C1Context_UsesOnlyThePrecedingSegment_AsBoundedContext` (context is `string.Empty` for the very first segment, then exactly the immediately preceding segment's text for the next — including across a new utterance) and `ConsecutiveUtterances_SecondUtterancesContextIncludesFirstUtterancesLastSegment`. This is a real product-design question this step surfaces but does not resolve: whether cross-utterance context helps or leaks stale topic drift into an unrelated new utterance is **UNVALIDATED** — no evidence either way was collected.

## 3. Speakability gating

**Deliberate simplification, not an oversight**: Steps 5.9–5.11 iteratively re-translate a still-moving candidate and must count N consecutive non-contradicting results before trusting it (the "CandidateForSpeech"/"Speakable" state machine). This step's segments are structurally different — a segment only exists here because `PrefixStabilityEngine` already committed it (its own unmodified "never retract a commit" guarantee), so each segment is translated **exactly once** and is eligible for TTS the moment that single translation call succeeds. Re-deriving Step 5.9's revision-counting heuristic for a value that cannot change would be inventing an unneeded protection, not providing one. This is documented, not silently assumed — see the class-level doc comment on `ConversationalStreamingPipelineExperiment`.

**Limitation this surfaces**: if `SemanticCompletionHeuristic` is ever wrong (declares a boundary that later speech contradicts), the self-correction described in §1 ("does not retract") means the pipeline will have already spoken the earlier version — PROVEN as a real, reproducible outcome by `SelfCorrectingSpeech_ContradictingLaterPartial_DoesNotRetractAlreadyCommittedSegment` (the delivered-count does not change after a contradicting later partial). This is an inherited trade-off from the frozen `PrefixStabilityEngine`, not introduced here.

## 4. TTS scheduling

**Design**: `DrainAsync` is a single-consumer, strictly-sequential FIFO loop — exactly one queued segment is translated+synthesized+delivered at a time. This trivially and structurally guarantees per-utterance ordering (§7) without a separate reordering buffer.

**Documented limitation, not claimed as optimal**: there is no synthesis parallelism. A slow translation or TTS call for segment N delays segment N+1's start even though N+1 could, in principle, translate concurrently. The live run (§8) makes this concrete: segment #1's translation did not start until segment #0 had already been fully delivered (T0→T1 for segment #1 was 4271ms — i.e., segment #1 sat queued while segment #0 was in flight). This is a real, measured cost of the chosen design, not hidden.

## 5. Cancellation / stale-generation protection

Generalizes Step 5.12's generation-guard pattern (checked at TTS submission and after synthesis) to the **whole pipeline**: every segment's `Generation` is captured at enqueue time and re-checked at three points — before processing starts, after translation completes, and after synthesis completes. PROVEN by three dedicated unit tests: `StaleGeneration_AtEnqueueTime_RejectedWithoutCallingTranslationOrTts` (rejected before any provider call — zero translation/TTS calls made), `StaleGeneration_DuringTranslation_DiscardedEvenThoughTranslationSucceeded` (a generation bump injected mid-translation via the fake provider's response factory still causes rejection, even though the translation itself succeeded), and `Cancellation_DuringTranslation_ReportedAsCancelled_NeverReachesSink`.

## 6. Duplicate prevention

PROVEN, and via a genuinely-reachable path (not a synthetic direct-call bypass): `ObserveFinal` resets the pipeline's per-utterance `SegmentSequence` counter to 0 whenever a new `UtteranceId` is observed — including a **replayed** Final for the *same* `UtteranceId`, since `ObserveFinal` always clears its "current utterance" tracker at the end of processing (to correctly support genuinely consecutive utterances, §10). A replayed Final therefore recycles `SegmentSequence=0`, colliding with the key of a segment already delivered — and the pipeline's `_delivered` guard correctly rejects it. `ReplayedFinalForSameUtteranceId_RecyclesSegmentSequence_RejectedAsDuplicate` proves this exact scenario end to end: first Final delivers (sink count 1), replayed Final produces a new `PipelineSegmentRequest` with the recycled sequence number, and `DrainAsync` rejects it as `RejectedDuplicate` (sink count stays at 1).

## 7. Ordering

Guaranteed structurally by the single-consumer sequential drain loop (§4), not by a separate reordering mechanism. PROVEN by `MultipleSegmentsAcrossUtterances_DeliveredToSinkInFifoOrder` (three utterances' segments delivered in submission order) and `MultipleSegmentsWithinOneUtterance_DeliveredInSequenceOrder` (segment sequence numbers within one utterance are monotonically non-decreasing at the sink).

## 8. Interruption / barging

**New concern for this step** (no direct precedent in Steps 5.7–5.12). Modeled the same way Step 5.12 modeled a generation-changing revision: the caller (not the pipeline) is responsible for detecting a barge-in and calling `AdvanceGeneration()`. Any segment already queued under the old generation is rejected the moment `DrainAsync` reaches it — PROVEN by `NewUtteranceWithGenerationBump_DiscardsPreviousUtterancesQueuedSegments` (the interrupted utterance's segment is rejected as stale, and — critically — it never even reaches the translation provider: `translation.Requests.Count == 1`, not 2) and `InterruptedSpeech_BargeInMidUtterance_OnlyNewUtteranceReachesSink`.

**Explicit boundary**: this experiment does NOT attempt to *detect* a barge-in (e.g., from simultaneous new audio input while old audio is still playing) — that is a production audio-capture/VAD concern entirely outside this experiment's scope (and outside Steps 5.7–5.12's scope too). This step only proves that *given* a correctly-timed `AdvanceGeneration()` call, stale content is reliably discarded.

## 9. Backpressure under fast speech

**Policy (ASSUMED, documented, not evidence-derived from real load)**: drop-oldest-non-final when `MaxQueueDepth` is reached. A Final segment is only ever dropped in the edge case where the queue is ALREADY saturated entirely with undrained Final segments (no non-final entry exists to evict) — Final segments are otherwise never dropped, mirroring `PrefixStabilityEngine`'s own "final content is never lost" guarantee.

PROVEN:
- `FastSpeech_ExceedingMaxQueueDepth_DropsOldestNonFinalSegments` — a realistic word-by-word 3-sentence stream against `MaxQueueDepth=1` produces multiple boundaries; every one but the most recent is evicted; every eviction is `DroppedForBackpressure`.
- `FinalSegment_NeverDroppedForBackpressure_EvenWhenQueueIsFull` — a queued non-final segment is evicted to make room for an arriving Final; the Final itself is never dropped.
- `FinalSegment_ExceedsBound_OnlyWhenQueueIsSaturatedEntirelyWithUndrainedFinals` — two consecutive Finals against `MaxQueueDepth=1` (nothing evictable) both survive; the bound is exceeded (`CurrentQueueDepth == 2`) rather than losing content.

**Metric captured**: `MaxObservedQueueDepth` — a running high-water mark, exposed on the experiment. In the live run (§10), it reached 2 against a configured bound of 5 (no drops occurred in that short two-segment scenario — MaxQueueDepth=5 was deliberately generous for the live smoke run, not stress-tested there).

**Limitation**: this bound is a simple in-memory list, not a true backpressure signal fed back to the ASR/capture layer — a production implementation would need to communicate backlog pressure upstream (e.g., to skip translating clearly-superseded content sooner), which this experiment does not attempt.

## 10. Consecutive utterances

PROVEN via two tests: `ConsecutiveUtterances_SecondUtterancesContextIncludesFirstUtterancesLastSegment` (the bounded-context history correctly carries the first utterance's segment into the second utterance's C1 context — an ASSUMED design choice, see §2) and `ConsecutiveUtterances_BoundaryTrackingResetsPerUtterance_NotCumulativeAcrossUtterances` (a short second utterance is evaluated on its own token count, not compared against the first utterance's much larger leftover count — proving `_lastEmittedBoundaryTokenCount` correctly resets per utterance while `_segmentHistory` deliberately does not).

## Metrics (12 named)

All timing fields are `null` (never a fabricated zero) whenever that stage was never reached — see `PipelineSegmentResult` in `ConversationalPipelineModels.cs`.

| # | Metric | Field | Status |
|---|---|---|---|
| 1 | Capture → first useful ASR | — | **UNAVAILABLE** in this experiment — there is no real microphone capture; the earliest available timestamp is the first synthetic/real partial event's own `Timestamp`, which is not a capture-time proxy. Not fabricated. |
| 2 | Stable segment detection | `T0SegmentReady` (on the request) | OBSERVED |
| 3 | Translation start | derived from `T0ToT1TranslationStartMs` | OBSERVED |
| 4 | Translation complete | derived from `T1ToT2TranslationCompleteMs` | OBSERVED |
| 5 | TTS start | derived from `T2ToT3TtsStartMs` | OBSERVED |
| 6 | First synthesized audio | derived from `T3ToT4FirstAudioMs` | OBSERVED |
| 7 | Complete synthesis | derived from `T4ToT5SynthesisCompleteMs` | OBSERVED |
| 8 | Total first-audio latency | `T0ToT4FirstAudioTotalMs` | OBSERVED |
| 9 | Total segment latency | `T0ToT6DeliveredTotalMs` | OBSERVED |
| 10 | Missing content | — | **UNVALIDATED** — no automated reconciliation harness was built for this step (Steps 5.5–5.11's token-multiset reconciliation was designed for single-shot cumulative retranslation comparisons, not a multi-segment streaming pipeline; building an equivalent was judged out of scope for this step — see "If any existing component is found insufficient, document the limitation" instruction). `PipelineUtteranceSummary.ApproxMissingTokenCount` exists as a typed placeholder (nullable `int?`) for a future step to populate; it is never set to a fabricated value by this experiment. |
| 11 | Duplicate content | — | Same as above — `PipelineUtteranceSummary.ApproxDuplicateTokenCount` exists as an unpopulated typed placeholder. Duplicate *delivery* (as opposed to duplicate *content*) is separately, fully proven — see §6. |
| 12 | Queue/backlog behavior | `MaxObservedQueueDepth`, `CurrentQueueDepth` | PROVEN/OBSERVED — see §9 |

## Live Azure run (OBSERVED, real credentials, real network calls)

Command: `dotnet run --project tools/VTTranslate.LiveTest/VTTranslate.LiveTest.csproj -- conversational-streaming-pipeline-test`

WDAC did **not** block this run in this environment (unlike Step 5.12's earlier attempts) — reported honestly, not assumed to generalize to any other environment. Real dedicated `AZURE_TRANSLATOR_KEY`/`AZURE_TRANSLATOR_REGION` and `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` credentials were used (loaded from environment variables only, never logged).

Actual console output (verbatim, metadata only — no source/translated/synthesized text):

```
=== Conversational streaming pipeline — real Azure Translator + real Azure TTS ===
  Boundary detected at partial #4: segment length=10 chars, context length=0 chars
  Boundary detected at partial #9: segment length=28 chars, context length=10 chars
  segment#0: outcome=DeliveredToTestSink T0->T1=7ms T1->T2=2228ms T2->T3=0ms T3->T4=1752ms T0->T4(firstAudio)=3986ms T0->T6(delivered)=4259ms bytes=54538 chunks=3 reason=n/a
  segment#1: outcome=DeliveredToTestSink T0->T1=4271ms T1->T2=135ms T2->T3=0ms T3->T4=1146ms T0->T4(firstAudio)=5552ms T0->T6(delivered)=5791ms bytes=124676 chunks=6 reason=n/a

Delivered to test sink: 2, dropped for backpressure: 0, max observed queue depth: 2
```

**Reading this data honestly**:
- Both segments were delivered successfully — real German→English translation and real TTS synthesis both completed for this one 2-sentence utterance (OBSERVED, one run, one input — not a statistically representative latency benchmark).
- Segment #0's translation took 2228ms — this is a single real network call's latency on this run, not a validated typical/average value.
- Segment #1's T0→T1 (4271ms) reflects **queueing delay**, not translation-call latency — it sat behind segment #0 in the strictly-sequential drain loop (§4's documented limitation, now empirically visible).
- Total first-audio latency for segment #0 was ~4 seconds, for segment #1 ~5.75 seconds (cumulative, since it queued behind #0). This is a single OBSERVED data point from one live run under one network condition — it is NOT a validated production-readiness latency figure, and per Step 5.12's own still-frozen status, **physical acoustic (speaker-to-ear) latency remains completely separate and UNVALIDATED** — this measurement stops at "handed to the in-memory test sink," never at an actual speaker.
- Zero segments were dropped for backpressure in this short, two-segment live smoke run — this run does not exercise or validate the backpressure eviction policy itself (that is unit-tested only, per §9; not re-validated live here).

## Final-decision answers

1. **Does the composed pipeline correctly turn a partial ASR stream into a spoken segment end to end?** Yes, PROVEN by unit tests with synthetic streams and OBSERVED once live with real Azure calls (one run, one utterance).
2. **Does bounded-context translation work when driven live across multiple segments/utterances?** OBSERVED for one live 2-segment case; the cross-utterance context-persistence design choice itself is ASSUMED, not independently evidence-validated for correctness of the resulting translations (no human/automated translation-quality judgment was performed — out of scope, consistent with every prior step in this series).
3. **Is speakability gating adequate without re-implementing Step 5.9's revision-counting?** Yes, structurally — proven not to be needed given this step's own boundary design (§3), with the trade-off (no self-correction after commit, inherited from the frozen `PrefixStabilityEngine`) explicitly documented, not hidden.
4. **Is single-consumer sequential TTS scheduling adequate?** It is CORRECT (never misorders, never double-delivers) but NOT latency-optimal — documented and empirically visible in the live run (§ above). A production implementation would likely need bounded parallelism; that redesign is out of this step's scope.
5. **Are cancellation/stale-generation, duplicate-prevention, and ordering guarantees solid?** Yes, PROVEN by dedicated unit tests for each, including two guards (duplicate-prevention, generation-during-translation) proven via their actual reachable trigger path, not a contrived bypass.
6. **Is the backpressure policy production-ready?** No — it is a documented, evidence-consistent (unit-tested) POLICY CHOICE, not a load-tested/production-validated mechanism. It has never been exercised under realistic high-throughput conditions, and it does not communicate backlog pressure upstream.
7. **Does anything here validate Step 5.12's still-frozen TTS feasibility findings?** No. Step 5.12 remains explicitly FROZEN and UNVALIDATED. This step's live TTS calls succeeded in THIS environment on THIS run, which is new, separate, Step-5.13-scoped OBSERVED evidence — it does not retroactively resolve Step 5.12's own WDAC-blocked status in whatever environment that validation is eventually performed in.

## Verification

- **Build**: `dotnet build src/VTTranslate.Core/VTTranslate.Core.csproj` → 0 errors, 0 warnings (PROVEN). `dotnet build tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj` → 0 errors, 0 warnings (PROVEN). `dotnet build tools/VTTranslate.LiveTest/VTTranslate.LiveTest.csproj` → 0 errors, 0 warnings (PROVEN).
- **Unit tests**: `dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj --filter "FullyQualifiedName~ConversationalStreamingPipelineExperimentTests"` → **26/26 passed** (PROVEN, not blocked by WDAC in this run). Full suite: `dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj` → **328/328 passed**, zero regressions to any prior step's tests (PROVEN).
- **Live Azure run**: see the dedicated section above — real, OBSERVED, not fabricated.
- **Production isolation**: `grep -c "ConversationalStreamingPipeline\|PipelineSegment" src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs src/VTTranslate.App/*.cs` → `0` for every file (PROVEN). Step 5.12's frozen files (`AzureStreamingTtsProvider.cs`, `StreamingTtsPipelineExperiment.cs`) confirmed unmodified.
- **Secret scan**: `grep -rEi "AZURE_[A-Z_]*KEY\s*=\s*['\"][A-Za-z0-9]{10,}"` across `src/`, `tools/`, `tests/`, `docs/` → no matches (PROVEN). New diagnostic log file (`test-results/logs/conversational-streaming-pipeline/`) checked for the live run's own source text (`"Guten Tag"`, etc.) → no matches; logs are metadata-only (PROVEN).
- **No LLM, naturalization, adaptive/self-learning memory, voice cloning, Google Meet, Android/iOS, production UI changes, or new production dependencies** were introduced — confirmed by inspection of every new/changed file listed in §0.

**STOP — Step 5.13 is complete. Waiting for review. Do not start Step 5.14 or integrate this experimental pipeline into production without explicit authorization.**
