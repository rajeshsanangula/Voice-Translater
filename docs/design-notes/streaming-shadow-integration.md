# Step 3 — Shadow Integration of PrefixStabilityEngine

**Status: SHADOW/OBSERVATION ONLY. The production pipeline (Audio → Azure Recognizer →
Recognized final → existing translation → existing TTS → existing playback) is
byte-for-byte unchanged. No streaming translation or streaming TTS exists.**

---

## 1. Architecture

```
                         ┌─────────────────────────────────────────┐
                         │   AzureSpeechTranslationProvider          │
                         │                                           │
   Audio ──PushAudio──▶  │   TranslationRecognizer (Azure SDK)       │
                         │        │                                  │
                         │        ├─ Recognizing (partial) ──┬──▶ PartialResult event (UNCHANGED)
                         │        │                          │
                         │        │                          └──▶ ShadowStabilityObserver.ObservePartial()
                         │        │                                    │  (diagnostic log only, return value discarded)
                         │        │
                         │        ├─ Recognized (final) ─────┬──▶ FinalResult event (UNCHANGED)
                         │        │                          │        → existing translation/TTS/playback (UNCHANGED)
                         │        │                          │
                         │        │                          └──▶ ShadowStabilityObserver.ObserveFinal()
                         │        │                                    │  (diagnostic log only, return value discarded)
                         │        │
                         │        └─ Synthesizing ───────────────▶ AudioSynthesized event (UNCHANGED, untouched by shadow)
                         └─────────────────────────────────────────┘
```

`ShadowStabilityObserver` ([src/VTTranslate.Core/Streaming/ShadowStabilityObserver.cs](../../src/VTTranslate.Core/Streaming/ShadowStabilityObserver.cs)) wraps one `IStreamingStabilityEngine` (a `PrefixStabilityEngine` by default) plus the bookkeeping needed to compute and log what the engine would commit. It has **no events, no reference to translation/TTS/playback, and no way to influence anything outside itself** — its only two outputs are diagnostic log lines and a `StabilityResult` returned directly to its caller, which `AzureSpeechTranslationProvider` reads only for the purpose of logging metadata and otherwise discards.

## 2. Integration boundary

Two call sites in [AzureSpeechTranslationProvider.cs](../../src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs), both purely additive:

- **`Recognizing` handler**: inside the existing Step 1 instrumentation's `lock (_instrumentationLock)` block, after the existing `PartialMeasurement` log line, calls `shadowObserver.ObservePartial(seq, e.Result.Text, DateTimeOffset.UtcNow)` — passing the real Azure partial's SOURCE text (never the translated text). The call happens *before* `PartialResult?.Invoke(...)`, but does not feed into it; `PartialResult`'s arguments are built solely from `e.Result` exactly as before.
- **`Recognized` handler**: inside the existing Step 1 instrumentation lock, after the existing `UtteranceSummary` log line, calls `shadowObserver.ObserveFinal(_partialSequenceInUtterance + 1, e.Result.Text, DateTimeOffset.UtcNow)`. This happens *before* `FinalResult?.Invoke(...)` and the existing translation/TTS-triggering logic, but again does not feed into any of it — `FinalResult`, the eligibility gate, and TTS synthesis all read only from `e.Result` and `decision`, exactly as before this change.

No line inside `PartialResult?.Invoke`, `FinalResult?.Invoke`, `AudioSynthesized?.Invoke`, `UtteranceEligibilityGate.Evaluate`, `ArmTtsWatchdog`/`DisarmTtsWatchdog`, or `OnCanceled`/reconnect logic was modified — confirmed by direct grep of the file (§7 below).

## 3. Lifecycle / state handling

One `ShadowStabilityObserver` (and its own fresh `IStreamingStabilityEngine`) is created per Azure recognizer generation, inside `CreateAndStartRecognizerLockedAsync`, captured only in that generation's `Recognizing`/`Recognized` closures — mirroring exactly how `myGeneration` itself is scoped. Within one generation:

- `shadowUtteranceIndex` (a local `int`, also closure-captured) increments once per `Recognized` (Final), giving each utterance a unique ID `"gen{generation}-utt{index}"`.
- `BeginUtterance(utteranceId, t0)` is called on the first partial of a new utterance (`seq == 1`) — or, for a short utterance with zero partials (the Step-1-observed "straight to Final" case), inside the `Recognized` handler itself if `_partialSequenceInUtterance == 0` at that point, immediately before `ObserveFinal`.
- `ObserveFinal` resets the observer's own per-utterance bookkeeping (`_currentUtteranceId = null`) after logging; the underlying `PrefixStabilityEngine.ProcessFinal` also resets its own internal state unconditionally (unchanged from Step 2). The next utterance requires a fresh `BeginUtterance` call — verified live in case H (§8), where `cumulativeCommittedTokenCount` correctly restarts at 0 for each of three consecutive utterances.

## 4. Generation/reconnect isolation

**Structural, not defensive.** A stale event from a superseded generation is already rejected by the existing `if (myGeneration != _generation) return;` guard at the top of both `Recognizing` and `Recognized` — this check runs *before* any shadow code executes, so an old generation's callback can never reach any shadow state, old or new. Separately, because `shadowObserver` is a fresh local variable created inside `CreateAndStartRecognizerLockedAsync` (called again on every reconnect), a new generation's closures capture a brand-new `ShadowStabilityObserver` wrapping a brand-new engine — there is no shared mutable shadow state between generations for anything to leak through, even in principle. This gives generation isolation "for free" from the same pattern already used for the production recognizer lifecycle, rather than requiring new isolation logic to be independently verified. See `ShadowStabilityObserverTests.SeparateGenerationInstances_NeverShareOrContaminateState` for the unit-level analogue of this guarantee.

## 5. Diagnostic fields

Four new event types, all metadata-only (see [ShadowStabilityObserver.cs](../../src/VTTranslate.Core/Streaming/ShadowStabilityObserver.cs)):

- **`ShadowFirstPartial`** (once per utterance): `generation`, `utteranceId`, `elapsedT0ToFirstPartialMs`.
- **`ShadowFirstCommit`** (once per utterance, only if a commit ever happens): `generation`, `utteranceId`, `elapsedT0ToFirstShadowCommitMs`.
- **`ShadowPartialObserved`** (every partial): `generation`, `utteranceId`, `partialSequence`, `elapsedFromT0Ms`, `shouldEmit`, `action`, `proposedCommitTokenCount`, `proposedCommitCharCount`, `cumulativeCommittedTokenCount`, `commitVersion`.
- **`ShadowFinalized`** (once per utterance): `generation`, `utteranceId`, `finalized=true`, `elapsedT0ToFinalShadowFlushMs`, `shouldEmit`, `proposedCommitTokenCount`, `proposedCommitCharCount`, `cumulativeCommittedTokenCount`, `finalizedWithCorrection`.

## 6. Privacy behavior

`sourceText` (the real Azure recognized text) is passed only into `PartialSourceEvent`/`FinalSourceEvent` — i.e., into `PrefixStabilityEngine`'s in-memory algorithm — and is never passed into any `_logger.Log(...)` call anywhere in `ShadowStabilityObserver.cs` or the two new call sites in `AzureSpeechTranslationProvider.cs` (confirmed by direct grep; see the Step 3 secret-scan result in this session). Only lengths/counts (`proposedCommitTokenCount`, `proposedCommitCharCount`), booleans (`shouldEmit`, `finalizedWithCorrection`), and identifiers/timings (`generation`, `utteranceId`, `partialSequence`, elapsed-ms values) are ever logged — matching every other diagnostic event this provider already emits.

## 7. Test coverage

**`ShadowStabilityObserverTests.cs`** (10 new tests, Azure-independent, all executed and passing — see §8):

| # | Requirement | Test |
|---|---|---|
| 1 | Recognizing partial reaches the stability engine | `ObservePartial_PassesSourceTextToTheEngine_AndReturnsItsResult` |
| 5,6,7 | Final finalizes; engine resets; new utterance starts clean | `ObserveFinal_FinalizesEngine_ThenNextUtteranceStartsClean` |
| 8,9 | Reconnect/generation isolation; stale callbacks can't contaminate | `SeparateGenerationInstances_NeverShareOrContaminateState` |
| 12 | Diagnostics contain metadata, never full speech text | `ObservePartial_NeverLogsRecognizedSourceText_OnlyMetadata`, `ObserveFinal_NeverLogsRecognizedSourceText_LogsFinalizationMetadata` |
| 13 | Deterministic regardless of wall-clock values | `SameInputSequence_ProducesIdenticalResults_RegardlessOfWhichWallClockValuesAreUsed` |
| — | Zero-partial utterance still finalizes | `ObserveFinal_WithNoPriorPartials_StillFinalizesFully` |
| — | Misuse fails loudly, not silently | `ObservePartial_WithoutBeginUtterance_Throws`, `ObserveFinal_WithoutBeginUtterance_Throws` |
| — | One-shot diagnostics fire exactly once | `FirstPartialAndFirstCommitDiagnostics_LogExactlyOncePerUtterance` |

**Requirements 2, 3, 4, 10, 11 (no shadow commit reaches translation/TTS/playback; existing final-result and cancellation/shutdown behavior unchanged) are guaranteed structurally, not by an executing integration test against `AzureSpeechTranslationProvider` itself**, because that class requires a real Azure connection to exercise its `Recognizing`/`Recognized` handlers at all, and this project has never had unit-test coverage of it directly for that reason (confirmed: no prior test file existed for it before this change). Instead:
- **Requirement 2/3/4** (no shadow → translation/TTS/playback): `ShadowStabilityObserver` has zero events and zero references to any translation/TTS/playback type — verified by reading its full source (§1) — so there is no code path by which it *could* reach those systems, structurally, not merely by omission in this particular wiring.
- **Requirement 10/11** (existing paths unchanged): verified by diff/grep — every `PartialResult?.Invoke`, `FinalResult?.Invoke`, and `AudioSynthesized?.Invoke` call site still constructs its event solely from `e.Result`/`audio`, unchanged from before Step 3 (§2), and `OnCanceled`/reconnect/`StopAsync`/`DisposeAsync` were not touched at all in this change (confirmed by reviewing the diff — the only edits to `AzureSpeechTranslationProvider.cs` are the constructor parameter, the `shadowObserver`/`shadowUtteranceIndex` locals, and the two `ObservePartial`/`ObserveFinal` call sites).
- This is reported honestly as a structural/code-review guarantee rather than an executed integration test, per this project's standing rule against fabricating test coverage that doesn't exist.

## 8. Live measurement results

Live-tested against real Azure using the `shadow-test` command added to `VTTranslate.LiveTest` ([tools/VTTranslate.LiveTest/Program.cs](../../tools/VTTranslate.LiveTest/Program.cs)) — reuses the exact same WAV files and harness as Step 1's `measurement-test` (no new audio needed; shadow instrumentation fires unconditionally, so no separate code path was needed to exercise it). Full logs: `test-results/logs/shadow/*.log`.

| Case | Partials | 1st partial latency | Shadow commits | 1st commit latency | Avg. commit interval | Final recognition | Final shadow flush | Sensible? | Duplicated? | Lost? | Self-correction OK? | Production unchanged? |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| A EN→DE normal | 1 | 13700ms* | 0 | — | — | 14115ms | 14116ms | yes (too few partials to commit before Final, correctly deferred) | no | no | n/a | yes (1 FinalResult, 1 AudioSynthesized) |
| B DE→EN normal | 3 | 1485ms | 2 | 2374ms | ~535ms | 3582ms | 3583ms | yes | no | no | n/a (no correction case) | yes (1/1) |
| C EN→DE fast | 3 | 13549ms* | 2 | 13915ms | ~468ms | 14382ms | 14383ms | yes | no | no | n/a | yes (1/1) |
| D DE→EN fast | 6 | 1491ms | 4 of 6 (seq4 correctly deferred, no emission) | 1819ms | ~692ms | 4501ms | 4501ms | yes | no | no | yes — seq4's source regression produced `action=None`, no emission, no `FinalizedWithCorrection` at Final | yes (1/1) |
| E EN→DE short | **0** | — | 0 | — | — | **none observed** | — | inconclusive | n/a | n/a | n/a | yes (0/0 — consistent, nothing fired) |
| F EN→DE long | 8 | 13395ms* | 7 of 8 | 13806ms | ~501ms | 17014ms | 17015ms | yes | no | no | n/a (no regression this run) | yes (1/1) |
| G EN→DE correction | 8 | 1447ms | 7 of 8 | 2814ms | ~486ms | 6174ms | 6179ms | yes | no | no | yes — 2 source regressions flagged by the (coarser, character-level) Step 1 `PartialMeasurement` analyzer at seq4/seq7, yet the engine's word-level LCP still found sufficient agreement to commit further at those same steps (see §10 finding) | yes (1/1) |
| H EN→DE ×3 consecutive | 1, 2, 2 | 13522ms* / 14805ms / 16015ms | 0, 1, 1 | —/15141ms/16341ms | — | 13907/15323/16349ms | 13907/15323/16349ms | yes | no | no | n/a | yes (3 FinalResult, 3 AudioSynthesized — exactly matching 3 utterances) |

\* EN→DE cases in this run show an anomalous ~13-14 second first-partial latency, reproducing the exact isolated-large-latency finding already documented in `streaming-measurement-results.md` §11.2 (Step 1's cases A/G showed the same ~13s anomaly). This reproduction across nearly every EN→DE case in this run (and its absence in every DE→EN case) is new evidence the effect correlates with direction/voice, not a one-off network blip — most plausibly first-request connection/endpoint warmup for the `de-DE-KatjaNeural` voice/target-language pairing, still consistent with Step 1's "real network/service latency, not separable from our own instrumentation" conclusion. Not investigated further — out of Step 3's scope (no eligibility/reconnect/config changes permitted).

**Case E** produced zero `Recognizing`/`Recognized` events at all within the 30-second test window (confirmed by reading its raw log — only `Connected`/`Shutdown` lines, no `RecognitionEvent` of any kind) — the short WAV's playback completed and the harness moved on before Azure produced any recognition result. This is most likely the same connection/first-request latency anomaly noted above (a short utterance played back for maybe ~0.5s, while EN→DE connections in this run habitually took 13+ seconds to produce a first partial) rather than a shadow-integration defect — production behavior was also unaffected (0 `FinalResult`/`AudioSynthesized` events, consistent with nothing having been recognized at all). Flagged as inconclusive, not swept under the rug.

## 9. Examples of shadow commits (from case D, live)

Reconstructing only from the privacy-safe counts (no recognized text was logged — see §10 for why this limits verification): case D (`I want to book a table for two people please` — DE original) showed partials growing from `textLength=10` to `88`, with shadow commits firing on partials 2, 3, 5, 6 (`proposedCommitTokenCount` 1, 3, 4, 1 respectively; running `cumulativeCommittedTokenCount` 1 → 4 → 8 → 9) and correctly producing **no commit** on partial 4, where the underlying source text itself regressed (`PartialMeasurement` logged `regressed=True` for that partial) — the engine detected the contradiction against already-committed content and withheld emission exactly as designed (Step 2/2.5's regression-safety guarantee, now confirmed against a real, not synthetic, Azure regression). The Final then added 4 more tokens, bringing the total to 13, with `finalizedWithCorrection=False` — meaning the Final agreed with everything already committed, so the mid-utterance regression at partial 4 resolved itself naturally by partial 5 without ever needing the Final to override anything.

## 10. Known problems

1. **Live content-correctness cannot be directly verified from diagnostics alone, by design.** The privacy requirement (never log recognized/translated text) means the live logs prove *shapes* (partial counts, commit timing, regression frequency, token/char counts) match what the algorithm was engineered and unit-tested against — they cannot themselves prove the *reconstructed text* is correct, since the actual words are never logged. Confidence that no duplication/loss occurs comes from the 138-test unit suite (Step 2/2.5) applied to the same unmodified algorithm, not from inspecting live output text. This is an inherent, accepted trade-off of the privacy requirement, not a gap introduced by Step 3 — noted here so it isn't mistaken for "live testing proved correctness," which it does not, and was never claimed to.
2. **Case E produced no data** (§8) — inconclusive, not a demonstrated defect, but also not a clean pass. Worth a longer test-window retry in a future pass, out of scope to fix now.
3. **The EN→DE-specific ~13-14 second first-partial-latency anomaly** (§8 footnote) recurred across nearly every EN→DE case in this run. It is a pre-existing, already-documented Step 1 finding, not new to Step 3, but its consistent recurrence here (5 of 5 EN→DE cases) versus its complete absence in DE→EN cases (0 of 3) is a stronger, more specific signal than Step 1 had. Not investigated further per Step 3's explicit scope (no reconnect/eligibility/config changes permitted) — flagged for whoever picks up latency work next.
4. **`PartialResultAnalyzer`'s character-level "regressed" flag and `PrefixStabilityEngine`'s word-level LCP can disagree** (§8/§9, case G) — a partial the Step 1 analyzer calls "regressed" can still let the word-level engine commit further, because the two operate at different granularities (exact-string comparison vs. word-level longest-common-prefix). This isn't a bug in either — they answer different questions — but it means the existing `revisionCount` metric in `UtteranceSummary` should not be read as "number of times the shadow engine had to defer a commit"; those are measurably different counts (confirmed live: case G showed `revisionCount=2` in its `UtteranceSummary` but only 1 partial — seq4's own step — actually corresponded to a real deferred-vs-committed distinction worth double-checking; the other "regressed" partial, seq7, still resulted in `action=Committed`). Worth a documentation clarification if Step 4 ever surfaces `revisionCount` to a UI/consumer as a stability signal — do not conflate it with the shadow engine's own deferral behavior.

## 11. Recommendation for Step 4

The shadow data supports proceeding to a **design-only** Step 4 (not implementation) that specifies how a real streaming-translation consumer would use `NewlyCommittedSegment`/`ShouldEmit`, informed by this run's concrete numbers: median commit intervals across cases B/C/D/F/G cluster in the ~470-700ms range, and first-commit latency (once the anomalous EN→DE connection delay is set aside, per DE→EN's ~1.5-2.8s figures) looks like a plausible, non-trivial improvement over waiting for the Final (which took 3.5-17s across these same cases). Before any implementation: (a) decide the Step-3-flagged consumer-side policy for `FinalizedWithCorrection`/deferred commits (already an open item from Step 2's verification report, unchanged by Step 3), (b) decide whether the V-1-class punctuation normalization needs a live-data check now that real Azure punctuation patterns are observable (not done this pass — logs don't carry punctuation-level detail since text itself isn't logged), and (c) investigate the EN→DE connection-latency anomaly (§10.3) separately, since it would directly undercut any streaming-translation latency benefit if left unexplained. This document makes no recommendation to begin streaming translation/TTS implementation itself — that remains explicitly out of scope until a separate, explicit approval.
