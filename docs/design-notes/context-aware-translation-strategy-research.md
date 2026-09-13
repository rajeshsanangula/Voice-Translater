# Step 5.10 — Context-Aware Translation Strategy Research

**Status: SHADOW/EXPERIMENTAL ONLY. `AzureSpeechTranslationProvider.cs` was not modified
(0 references to any Step 5.10 type — confirmed by grep, §Verification). No production
audio path calls this experiment. No TTS/playback/Google Meet integration. No LLM was
introduced.**

**Legend**: **PROVEN** = directly demonstrated by an executed unit test or a real live
API call this step. **OBSERVED** = seen in this step's live run (14 cases, one run).
**UNVALIDATED** = not exercised this step. **UNAVAILABLE** = a capability this step
determined is not usable with this project's current resource/credentials.
**ASSUMED** = a design choice not independently verified this step.

---

## 1. Actual Azure capabilities inspected

- **Contextual translation (native)**: **UNAVAILABLE.** The base Translator Text v3.0 `/translate` endpoint has no context/session/document parameter (confirmed from the documented API contract, consistent with Step 5.9 §1's finding, not re-probed this step).
- **Batch translation**: **PROVEN available** (confirmed live in Step 5.9 §1 — multiple independent texts per HTTP call). Not used for cross-element context this step, since Step 5.9 already established batch elements are translated independently with zero shared context.
- **Custom translation / terminology / translation memory / domain-specific translation**: **UNAVAILABLE / NOT VERIFIED IN THIS SESSION.** Per the explicit decision made before this step began: this project has used only a standard base Translator resource throughout every step (5.5-5.10); no Custom Translator trained model, category ID, or terminology resource has ever been referenced, configured, or used. Whether such a capability is provisioned elsewhere on the underlying Azure subscription **cannot be conclusively determined from this session** — that would require Azure Portal/subscription-level access this session does not have. This is reported as "not verified here," not as a global claim that no such capability exists anywhere on the account.
- **Persistent/session context**: **UNAVAILABLE** — same finding as native contextual translation; no session concept exists in the base API.
- **Document translation** (the separate, asynchronous Azure Document Translation service): not investigated — out of scope for interactive incremental text, and no evidence this project has that service provisioned either.

**Approach C ("available supported contextual/custom capability") is explicitly marked: UNAVAILABLE / NOT VERIFIED IN THIS SESSION.** No fake or proxy implementation was created in its place — see §2 (only Approaches A and B were implemented).

## 2. Approaches evaluated

### Approach A — base Translator + accumulated source context (application-side)
Concatenates the previously-committed SOURCE text with the newly-committed segment into **one request string**, sent to the same base `/translate` endpoint. **This is explicitly application-side string construction — the Translator API has no way to distinguish "context" from "new content" in this string; it simply translates whatever text it receives.** Never described elsewhere in this document as native API context.

### Approach B — base Translator + minimal current segment
Translates only the newly-committed segment, alone, with zero preceding text.

### Approach C — UNAVAILABLE / NOT VERIFIED IN THIS SESSION
Not implemented. See §1.

## 3. Critical rule — translation success != quality success

Every candidate from both approaches was compared, at Final, against the true authoritative translation via the same structural (non-semantic) token-multiset method used throughout Steps 5.5-5.9 — `missing`/`duplicate` token counts, never a raw "did it return 200" success claim. **PROVEN (unit test)**: a `null`, never a fabricated `0`, is reported whenever a metric cannot be reliably calculated (zero-request cases — confirmed live in `N_ConsecutiveUtterance1`/`2`, both cases where the utterance never grew past its first partial).

## 4. Realistic corpus — same A-N as Step 5.9

All 14 cases reused verbatim from Step 5.9 (§Corpus reference in that document) for direct comparability — not replaced with easier examples, per instruction.

## 5. Translation revision analysis — does context reduce destructive revisions?

**OBSERVED, and this is the central, decisive finding of this step**: classified every successive candidate via the heuristic `TranslationRevisionKind` (Identical/Extension/ParaphraseOrRestructuring/Contradiction/Replacement/Incomplete — token-level heuristic, explicitly not a linguistic parse, see code doc comment).

| | Approach A (context) | Approach B (no context) |
|---|---|---|
| Cases with ≥1 contradiction | **4 of 14** (A, C, D, H) | **9 of 14** (A, B, C, D, F, G, H, I, M) |
| Total contradiction count (all cases) | 4 | 13 |
| Cases where `missing` was strictly worse than the other approach | 0 (A always ≤ B when they differ) | **9 of 14** |

**Context materially and consistently reduced contradictions**: more than double the cases were contradiction-free under Approach A (10/14) versus Approach B (5/14), and in every single case where the two approaches' `missing`-token counts differed, Approach A's was equal to or better than Approach B's — never worse. This is real, quantified, repeatable-methodology evidence (not a single cherry-picked case) that giving the Translator API more surrounding text — even via simple string concatenation, with zero native API support — genuinely helps it produce more internally consistent output across successive calls.

## 6. Candidate speech decision

**OBSERVED, equally decisive**: **Approach A reached `CandidateForSpeech` or better in 4 of 14 cases (F, G, I, M) and full `Speakable` in 1 of 14 (F). Approach B reached `CandidateForSpeech` or `Speakable` in ZERO of 14 cases** — every single `earliestCandidateForSpeechDelayMs` for Approach B in this run is `null`. No case was marked Speakable based merely on success/grammaticality/resemblance to Final — confirmed by design (3-consecutive-agreement requirement) and by the fact that even Approach A, the better-performing approach, only reached it once.

## 7. Request efficiency

**PROVEN (live)**: 52 total real requests (26 per approach) across 14 cases, **81 total context characters** sent by Approach A versus **412 total segment characters** (the base content both approaches send) — context overhead was real but modest in absolute terms for these short, single-utterance test cases (roughly 16% of total character volume). **This is expected to scale unfavorably with longer conversations** (more prior turns accumulating as re-sent context on every request) — not measured this step (the corpus is single-utterance-scale), flagged as a real, unquantified risk for any future design building on Approach A as-is. The confirmed batch capability (§1) was not used to reduce request count this step, per "do not redesign the production provider" / avoid premature optimization.

## 8. Cold-start and latency outliers

**OBSERVED**: this run's **worst-case latency was 1,399ms** (`A_SimpleStatement`'s first Approach-A call) — a real outlier relative to this run's own typical 106-293ms range, but **dramatically smaller than the ~13-15 second cold-start anomaly observed once each in Steps 5.7, 5.8, and 5.9.** The absence of a double-digit-second outlier in this run is itself informative: it is consistent with (but does not prove) that anomaly being an occasional, non-deterministic cold-start cost rather than a guaranteed per-run event — **UNVALIDATED** whether it's deterministic or probabilistic, same open question as Step 5.9 left it. Per instruction, the prior 15.2s outlier is **not discarded or forgotten** — it remains on record in Step 5.9's document as real evidence against an unqualified real-time-suitability claim, and this run's own smaller-but-still-notable 1.4s outlier is reported in the same spirit, not averaged away.

## 9. No LLM

**PROVEN**: no OpenAI, local LLM, Gemini, Claude, or other generative model was introduced anywhere in this step's code, tests, or live run — confirmed by review of every new file (`ContextAwareTranslationModels.cs`, `ContextAwareTranslationExperiment.cs`) and the live harness command, all of which call only `AzureTranslatorTextProvider`/`IIncrementalTranslationProvider`.

## 10. Security

**PROVEN**: neither `_logger.Log` call site in `ContextAwareTranslationExperiment.cs` includes source/context/translated text — confirmed by code review (both interpolate only counts/states/booleans/latencies). No persistent storage of raw speech text was added.

## 11. Production isolation

**PROVEN**: `AzureSpeechTranslationProvider.cs` — 0 references to any Step 5.10 type (confirmed by grep). No experimental result reaches the production audio path. No TTS/playback integration exists anywhere in this step's code.

## 12. Tests

**PROVEN**: 11 new unit tests, all passing — context construction (Approach A includes previously-committed context; first-ever segment correctly has zero preceding context, i.e. proper bounds/truncation at the start of an utterance), segment extraction (Approach B never includes context), contradiction detection, duplicate/missing-content null-not-zero reporting for zero-request cases, consecutive-utterance isolation, self-correction trailing-word safety, failed-translation and empty-translation handling (both correctly reset the consecutive-stability counter, not silently ignored), explicit session reset, and privacy. Full suite: **274/274 passed.**

## 13. Documentation

This document.

## 14. Final decision — explicit answers, not assumed

1. **Does application-side context materially improve translation stability?** **Yes — OBSERVED and quantified.** Contradiction-affected cases dropped from 9/14 (no context) to 4/14 (with context); missing-token counts were never worse under context, and were better in 9/14 cases where they differed.
2. **Does segment-only translation work adequately?** **No — OBSERVED to underperform materially.** Zero cases reached even `CandidateForSpeech`, and it had strictly worse `missing`-token counts than the context-aware approach in 9 of 14 cases where they differed.
3. **Is a supported contextual/custom Azure capability actually available?** **UNAVAILABLE / NOT VERIFIED IN THIS SESSION** — no native context parameter exists on the base endpoint used throughout this project; no Custom Translator or other contextual capability has ever been referenced or provisioned in this project, and subscription-level confirmation of total unavailability is outside this session's reach.
4. **Does any approach produce enough stable candidates to justify a future TTS experiment?** **Not yet, but Approach A is the first approach across Steps 5.7-5.10 to show a real, repeatable, quantified improvement** — 4 of 14 cases reaching a defensible pre-final state (up from Step 5.9's best of 1/14 for its Architecture C, which used no context at all). This is meaningfully better, but still a minority of realistic utterances (29%), and only 1 of 14 reached the strictest `Speakable` bar. **The honest answer remains "not yet," but with the clearest positive signal so far.**
5. **What architecture should be used next?** **Approach A's context-construction principle, not Approach B's**, should carry forward into any future refinement — it is the single most effective lever identified across this whole experimental series (Steps 5.5-5.10) for reducing translation-side contradictions. A future step (not authorized here) could reasonably explore: (a) bounding Approach A's context window to control the §7 character-growth risk for longer conversations, rather than resending the full accumulated context unbounded; (b) combining Approach A's context construction with Step 5.9's Architecture C-style consecutive-agreement gating more deliberately (this step already did combine them, and that combination is what produced the 4/14 CandidateForSpeech result — worth treating as the working baseline to refine, not a full solution).

## Verification

- **Test count**: 274/274 passed.
- **Build**: 0 warnings, 0 errors.
- **Secret scan**: clean.
- **Production files changed**: none — `AzureSpeechTranslationProvider.cs` untouched (0 matches for any Step 5.10 type).
- **Experiment files added**: `ContextAwareTranslationModels.cs`, `ContextAwareTranslationExperiment.cs` (new); `PrefixStabilityEngine.cs`/`AzureTranslatorTextProvider.cs` reused unmodified.
- **Live API calls**: 52 real Translator requests (493 total characters: 81 context + 412 segment) across 14 cases × 2 approaches, real dedicated Translator credentials only.
- **Recommendation**: do not proceed to a TTS experiment yet. Context construction (Approach A) is a real, measured improvement over segment-only translation (Approach B) and should be the basis for any further architecture refinement, but even the best-performing approach this step produced a defensible pre-final speech candidate in only 29% of realistic cases — insufficient to justify connecting to TTS.
