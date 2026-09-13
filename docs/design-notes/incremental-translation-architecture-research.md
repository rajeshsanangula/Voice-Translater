# Step 5.9 — Incremental Translation Architecture Research

**Status: SHADOW/EXPERIMENTAL ONLY. `AzureSpeechTranslationProvider.cs` was not modified
(0 references to any Step 5.9 type — confirmed by grep, §Verification). No production
audio path calls this experiment. No experimental output was made audible or reached
TTS/playback/Google Meet.**

**Legend**: **PROVEN** = directly demonstrated by an executed unit test or a real live
API call this step. **OBSERVED** = seen in this step's live run (14 cases, one run) — a
real data point, not a broad claim. **UNVALIDATED** = not exercised this step.
**ASSUMED** = a design choice not independently verified this step.

---

## 1. Actual Azure Translator API capabilities discovered

**PROVEN (live, one real call)**: the genuine Translator Text v3.0 API **supports batch requests** — a single HTTP call with a multi-element `Text` array returns independent translations for each element (confirmed: sending `[{"Text":"Hello there"},{"Text":"How are you"}]` returned two correctly-separated translations in one 200 response). This is a real, documented, already-latently-available capability this project's `AzureTranslatorTextProvider` has never used (it always sends a single-element array) — a genuine, concrete opportunity for a future step to reduce request count for architectures needing multiple related segments translated together, **not implemented this step** (avoiding premature optimization per instruction).

**Based on the documented public API contract (not fabricated, not probed for undocumented behavior)**: the base `/translate` v3.0 endpoint has **no session/context/document parameter** — each call is genuinely stateless; there is no way to tell the service "this text continues from an earlier call." **Sentence/document-level context, translation memory, and customization** are provided only by the separate "Custom Translator" product, which requires a distinct training/deployment workflow (uploading parallel documents, training a custom model in the Azure portal) — **this project has no evidence such a custom model has been provisioned for this Translator resource**, and none was created or assumed this step. **Efficient incremental requests**: no native "continue this translation" mechanism exists; every call re-translates its given text from scratch. **Language-specific behavior**: the API accepts standard BCP-47-style language codes; no incremental-translation-specific behavior differences between language pairs were discovered (or looked for) beyond what's already documented in this project's own live data (§4).

## 2-3. Isolation and the three-state model

**PROVEN**: `AzureSpeechTranslationProvider.cs` was not modified (§Verification). Three genuinely separate state machines are implemented as distinct enums in [ProvisionalTranslationModels.cs](../../src/VTTranslate.Core/Streaming/ProvisionalTranslationModels.cs): `ProvisionalSourceState` (Provisional/Stable/Final), `ProvisionalTranslationState` (Draft/StableDraft/Revised/Authoritative), and `SpeechReadiness` (NotSpeakable/CandidateForSpeech/Speakable) — never collapsed. **PROVEN (unit test + live)**: a `Draft` never becomes `Speakable` directly — `SpeechReadiness` only ever advances past `NotSpeakable` through the explicit, conservative criterion in Architecture C (§5).

## 4. Overlapping incremental translation — both directions

Realistic growing-prefix sequences (`I` → `I would` → `I would like` → ...) were run in both directions across all 14 required cases (§8), 5 with German source, 9 with English source, via the real Translator API. Per-request metadata (source prefix length, request number, latency, changed-from-previous, supersedes-previous, previous-still-valid) was recorded for every call — see §6 for the aggregate results.

## 5. Three reconciliation architectures — results

### Architecture A — cumulative retranslation
Translates the full cumulative committed SOURCE on every stable commit (the same baseline Steps 5.7/5.8 measured). **OBSERVED (live)**: reproduces the same pattern as those steps — `missing` tokens nonzero in every non-trivial case (structural consequence of the trailing-word buffer), small nonzero `duplicate` counts (1-3 tokens) from the same non-identical-translation-of-overlapping-text artifact already documented in Step 5.7 §9. Never gated for speech-readiness in this experiment (that's Architecture C's job) — always `Draft`, always `NotSpeakable`.

### Architecture B — overlapping segment translation
Translates a fixed 4-word sliding window (not the full cumulative) and attempts to reconcile consecutive segments via longest-token-overlap detection, appending only the non-overlapping remainder. **OBSERVED, and this is a genuine negative finding**: Architecture B's `duplicate` counts were **consistently equal to or worse than** Architecture A's across almost every case, and dramatically worse in two (`B_LongSentence`: A=2 duplicate vs. B=7; `H_GermanSeparableVerb`: A=0 vs. B=3). The naive tail/head token-overlap-detection approach **cannot reliably join real Azure Translator output** — translated segment boundaries do not line up cleanly at the word level even when the underlying source segments genuinely overlap, because Translator's word choice/ordering for a short, context-free 4-word window is not guaranteed consistent with the same words appearing in a different (also context-free) window. **The one real advantage observed**: Architecture B sent measurably fewer characters per utterance in longer cases (`K_FastSpeech`: A=43 chars vs. B=27; `B_LongSentence`: A=70 vs. B=52) — a genuine, quantified efficiency benefit, but traded against materially worse reconciliation quality.

### Architecture C — provisional draft + authoritative final
Translates the full cumulative SOURCE like Architecture A, but gates `SpeechReadiness` behind an explicit, conservative criterion: `StableDraft` requires the new translation to not contradict (word-level) the immediately preceding one; `CandidateForSpeech` requires 2 CONSECUTIVE non-contradicting translations; `Speakable` requires 3 CONSECUTIVE (see `MinConsecutiveStableForSpeakable`). **OBSERVED (live), the most important finding of this step**: **Architecture C detected a genuine word-level contradiction between consecutive real Translator calls in 5 of 14 cases** (`A_SimpleStatement`, `B_LongSentence`, `C_Question`, `D_ModalConstruction`: 1 contradiction each; `H_GermanSeparableVerb`: 2) — meaning the real Azure Translator API's own output for a growing cumulative source is **not always a clean word-level extension of its own previous output**, even for the exact same growing-prefix pattern that worked cleanly in other cases. This is real, quantified evidence that a naive "just trust cumulative retranslation" approach (Architecture A) would produce audibly-flickering/contradictory speech in roughly a third of realistic utterances if ever connected to TTS without gating — directly justifying the need for Architecture C's conservative gate. **`Speakable` was reached in only 1 of 14 cases** (`F_GermanSimpleStatement`); `CandidateForSpeech` (2 consecutive agreements, not yet 3) was reached in 3 more (`G`, `I`, `M`) — so a genuinely defensible pre-final speech candidate existed in 4 of 14 cases (29%), and a *fully* defensible (3-consecutive) one in only 1 of 14 (7%).

## 6. Critical measurement (aggregate)

| Metric | Architecture A | Architecture B | Architecture C |
|---|---|---|---|
| Cases with ≥1 mid-stream request | 12/14 | 12/14 | 12/14 |
| Total requests (all 14 cases) | 26 | 26 | 26 |
| Cases with any duplicate tokens | 11/14 | 12/14 | 9/14 |
| Cases with any contradiction | 0/14 (not gated) | 0/14 (not gated) | **5/14** |
| Cases reaching CandidateForSpeech+ | n/a (ungated) | n/a (ungated) | **4/14** |
| Cases reaching Speakable | n/a (ungated) | n/a (ungated) | **1/14** |

`EarliestStableDraftDelayMs`/`EarliestCandidateForSpeechDelayMs` were correctly `null` (never fabricated as `0`) for every one of the 10 Architecture C cases that never reached that state — verified live and by dedicated unit test (`ZeroRequestArchitecture_ReportsNullMetrics_NeverFabricatedZero`). Where reached, the delay from first partial ranged 539-1417ms (F: 1417ms, G: 586ms, I: 539ms, M: 600ms) — genuinely earlier than most of these cases' Final latency (148-550ms *after* the last partial, on top of however long ASR itself took to reach Final in a real pipeline — not measured here, since this experiment is text-only).

## 7. Critical safety rule — no artificial claims

**PROVEN**: a translation is never marked `Speakable` in this experiment merely because a call succeeded, looked grammatical, or the source was stable — confirmed by design (the gate requires 3 *consecutive* non-contradicting real translations) and by live data (§5-6: even `CandidateForSpeech`, the *weaker* bar, was reached in only 4 of 14 cases). No case in this run was marked Speakable based on a single successful call.

## 8. Realistic test corpus (A-N)

All 14 required cases were run live — see §5-6 for aggregate results and the raw log (`test-results/logs/provisional-translation-architecture/*.log`) for the full per-step detail.

## 9. Naturalness (subjective, experimental observation only)

A small number of the live translations were read directly from console/log output for this assessment — **explicitly labeled as subjective, non-blinded, single-reviewer observation, not a formal guarantee**: Architecture C's contradiction cases (e.g. `H_GermanSeparableVerb`, 2 contradictions) plausibly correspond to Azure's translation shifting *grammatically* as more of the separable-verb clause arrived (a real, expected linguistic phenomenon for German — the meaning of "rufe...an" genuinely changes once "an" appears), not an arbitrary API inconsistency; this is consistent with, and provides tentative supporting color for, the correctness of gating translation-side stability *in addition to* source-side stability, though this observation should not be treated as proof of a specific linguistic mechanism without further review.

## 10. Azure API efficiency / request amplification

**PROVEN (live)**: this run made **78 real translation requests totaling 866 characters** across 14 cases × 3 architectures (26 requests/866÷3≈289 chars per architecture on average, though character totals differ by architecture as shown in §5's Architecture B discussion). Running three architectures side-by-side, as this experiment deliberately does for comparison purposes, **triples request volume relative to a single production architecture** — an expected, not surprising, property of a comparison experiment, not a claim about a future production design's cost. Duplicate source material sent across requests is structural for Architecture A/C (the full cumulative is resent, including already-translated words, on every commit — the "amplification" is real: an utterance with N commits sends the first word (N) times over its N cumulative calls) and explicitly reduced for Architecture B (only ever sends the last 4 words) at the cost of reconciliation quality (§5). **No optimization was attempted this step** (e.g. using the newly-discovered batch capability, §1, to reduce round-trips) — flagged as a real, available lever for a future step, not exercised here per "do not optimize prematurely."

## 11. Cold-start and latency outliers

**OBSERVED, not silently discarded**: `A_SimpleStatement`'s very first call this run measured **15,194ms** (~15.2 seconds) — consistent with the same cold-start-class anomaly observed once each in Steps 5.7 (13.8s) and 5.8 (13.4s, plus a smaller 3.0s second spike) — always at or near the very first call of a fresh process. `D_ModalConstruction`'s first call measured 953ms, a smaller, second-tier outlier not at the very start of the run. **Normal observed latency** (the other 76 of 78 calls): consistently **130-950ms**, most commonly 150-350ms. **UNVALIDATED**: whether the ~13-15 second cold-start pattern is deterministic (always exactly the first call) or probabilistic was not tested this step (would require multiple full fresh-process runs) — flagged, not assumed. **This step does not claim real-time suitability** — the presence of a double-digit-second outlier on cold start, unexplained and unaddressed, is itself evidence against an unqualified real-time claim, stated explicitly rather than averaged away.

## 12. Security

**PROVEN**: neither `_logger.Log` call site in `ProvisionalTranslationArchitectureExperiment.cs` includes source or translated text — confirmed by direct code review (both interpolate only counts/states/booleans/latencies). No persistent storage of raw speech text was added.

## 13. Tests

**PROVEN**: 14 new unit tests, all passing (incremental prefix progression driving all three architectures, Architecture A never reaching Speakable, Architecture B's sliding-window vs. full-cumulative distinction and non-doubling reconciliation, Architecture C's contradiction detection and Speakable-only-after-minimum-consecutive-agreement progression, source self-correction safety, Final authoritative reconciliation, null-not-zero metrics for zero-request cases, consecutive-utterance isolation, failed/empty Translator responses, explicit reset, privacy). Full suite: **263/263 passed**.

## 14. Documentation

This document. See also `docs/design-notes/streaming-translation-decision-experiment.md` (Step 5.7) and `docs/design-notes/semantic-segment-translation-experiment.md` (Step 5.8) for the prior steps this one builds directly on.

## 15. Final recommendation — explicit answers, not assumed

1. **Can provisional translation be generated meaningfully earlier than final ASR?** **Yes, OBSERVED** — Architecture C's `CandidateForSpeech`/`Speakable` states, when reached, occurred 539-1417ms after the first source partial, genuinely before the utterance's Final. But this was only in 4 of 14 (CandidateForSpeech) and 1 of 14 (Speakable) cases — "meaningfully earlier" is true when it happens, but it does not happen for most realistic utterance shapes in this data (consistent with Step 5.8's finding).
2. **Can that provisional translation be reconciled reliably?** **Partially — OBSERVED to depend heavily on architecture.** Architecture A/C's cumulative approach reconciles acceptably (small duplicate counts, structural missing counts already understood from Step 5.7). Architecture B's overlapping-segment join **does not** reconcile reliably — it was measurably worse than the simpler cumulative approach in this data, a genuine negative result.
3. **Can a defensible subset become CandidateForSpeech?** **Yes, but narrowly** — 4 of 14 cases (29%) reached a defensible (2-consecutive-agreement) state; only 1 of 14 (7%) reached the stricter 3-consecutive `Speakable` bar. "A defensible subset exists" is true; "most utterances produce one" is not, per this data.
4. **Is the latency/request overhead acceptable?** **UNVALIDATED as a production judgment** — this experiment measured real numbers (78 requests/866 chars for a 14-case, 3-architecture comparison; 130-950ms normal per-call latency; one 15.2s cold-start outlier) but "acceptable" requires a production latency/cost budget this research step was not given and does not assume. The raw numbers are reported for someone else to judge against such a budget.
5. **Is there enough evidence to proceed to a TTS experiment?** **No — not yet, per this step's own data.** The core finding (§5-6) is that real Azure Translator output contradicts its own immediately-preceding output for the same growing source in roughly a third of realistic cases (5/14) — a naive streaming-TTS connection without Architecture C's gate would very plausibly produce audibly contradictory speech close to a third of the time. Even WITH the gate, a genuinely safe (`Speakable`) candidate existed early enough to matter in only 1 of 14 cases. This is a **measured, not assumed, "not yet."**
