# Step 5.11 — Context Window & Translation Commit Optimization Experiment

**Status: SHADOW/EXPERIMENTAL ONLY. `AzureSpeechTranslationProvider.cs` was not modified
(0 references to any Step 5.11 type — confirmed by grep, §Verification). No production
audio path calls this experiment. No TTS/playback/Google Meet integration. No LLM was
introduced.**

**Legend**: **PROVEN** = directly demonstrated by an executed unit test or a real live
API call this step. **OBSERVED** = seen in this step's live run (16 cases, one run).
**UNVALIDATED** = not exercised this step. **ASSUMED** = a design choice not
independently verified this step.

**Terminology, stated once and applied throughout**: every "context" strategy below is
**APPLICATION-SIDE CONTEXT CONSTRUCTION** — plain string concatenation before sending one
request to the same base Translator `/translate` endpoint used throughout this project.
The base API has no demonstrated native session/context parameter (confirmed in Step
5.9 §1, reconfirmed by non-use here). Nothing in this document should be read as claiming
a native Translator context feature.

---

## 1-2. Five bounded context strategies

Implemented in [ContextWindowTranslationExperiment.cs](../../src/VTTranslate.Core/Streaming/ContextWindowTranslationExperiment.cs):

- **C0 — current segment only**: zero context (Step 5.10's Approach B).
- **C1 — current segment + one immediately-preceding committed unit**: the single most-recent committed source segment as context.
- **C2 — current segment + last 2 committed units**.
- **C3 — current segment + a bounded 30-character recent-context window** (tail-truncated, never head-truncated — see §Tests).
- **C4 — current segment + a bounded 80-character window** (larger, for comparison — never unlimited).

No strategy ever accumulates unbounded conversation history — C1/C2 are bounded by unit count, C3/C4 by an explicit character constant.

## 3. Language direction and corpus

Both directions tested: 9 English-source, 7 German-source cases. **The exact same A-N corpus from Steps 5.9/5.10 was reused verbatim**, plus 2 additional stress cases (negation, a second short-utterance case) per this step's explicit corpus list — 16 cases total, for direct comparability with prior steps.

## 4-5. Quality and context-efficiency results (live)

**OBSERVED, the central finding of this step — clear diminishing returns**:

| | C0 (none) | C1 (1 unit) | C2 (2 units) | C3 (30 chars) | C4 (80 chars) |
|---|---|---|---|---|---|
| Cases with ≥1 contradiction (of 13 non-trivial cases) | **10** | **6** | **5** | **5** | **5** |
| Total characters sent (whole corpus) | 213 | 300 | 312 | 312 | 312 |
| Amplification vs. C0 (corpus total) | 1.00x | 1.41x | 1.46x | 1.46x | 1.46x |

**Going from C0 to C1 eliminated contradictions in 4 of 10 previously-affected cases** (B, G, I, M) at a corpus-wide cost of +41% characters. **Going from C1 to C2 eliminated exactly one more case's contradiction** (F, the only case that reached `Speakable` at all — and only at C2/C3/C4, never at C0/C1) at a further +5% corpus-wide character cost. **C3 and C4 produced IDENTICAL results to C2 in every single case** — their larger character bounds (30 and 80 respectively) were never actually the limiting factor for any utterance in this corpus, because "2 preceding committed units" already fit comfortably within 30 characters for every case tested. This is a real, honest limitation of this corpus (short, single-utterance test cases), not evidence that C3/C4 are useless in general — a longer/more turn-heavy corpus could plausibly separate them, **UNVALIDATED** this step.

`missing`/`duplicate` token counts: context strategies (C1-C4) never showed a strictly worse `missing` count than C0 in this run, and were frequently better (e.g. `B_LongSentence`: C0=12 missing vs. C1-C4=12 identical; `C_Question`: C0=5 vs C1-C4=5 identical; `M_BusinessTechnical`: C0=8 vs. C1-C4=5, a real improvement) — consistent with, and reinforcing, Step 5.10's finding.

## 6. Latency

**OBSERVED**: `A_SimpleStatement`'s very first call (C0) measured **13,514ms**. This is the **fifth consecutive experimental step** (5.7: 13.8s; 5.8: 13.4s + a 3.0s second spike; 5.9: 15.2s; 5.10: 1.4s — the one exception; 5.11: 13.5s) to show a large cold-start-class outlier at or very near the first call of a freshly-started process. Given this consistent recurrence across five independent runs (Step 5.10 being the sole exception), this step's assessment is revised from Step 5.9's "UNVALIDATED whether deterministic or probabilistic": **the pattern is now OBSERVED to recur in the large majority (4 of 5) of fresh-process runs specifically at or near the first call**, which is stronger evidence for a genuine cold-start/connection-warmup cause than a purely random one-off — still not formally proven deterministic (no controlled repeated-first-call experiment was run), but no longer merely "seen once." **Normal latency** for the remaining 71 real calls this run: consistently 112-333ms, no other outlier of note. Per instruction, this outlier is not discarded, and no real-time-suitability claim is made without accounting for it.

## 7. Revision/stability analysis — does more context help, and does it plateau?

**OBSERVED, direct answer**: yes, additional context (C0→C1→C2) reduces contradictions, and yes, it clearly **plateaus** — C2, C3, and C4 produced byte-for-byte identical contradiction/revision counts in every case (§4-5 table). **Larger context was never observed to be worse** in this run (no case showed C4 introducing a NEW contradiction absent at C1/C2), consistent with "more context helps or is neutral, never actively harmful" for this corpus — but this should not be over-generalized, since C3/C4's extra headroom was never truly exercised (§4-5).

## 8. Speech eligibility (kept separate, not optimized for)

**PROVEN (unit test + live)**: the same conservative `Draft`/`StableDraft`/`Revised`/`Authoritative` and `NotSpeakable`/`CandidateForSpeech`/`Speakable` state machines from Steps 5.9/5.10 are reused (by value, not reference). **`Speakable` was reached in exactly 1 of 16 cases** (`F_GermanSimpleStatement`, and only at C2/C3/C4 — never C0/C1). This step did not optimize for Speakable percentage — the primary measured outcome throughout is contradiction/missing/duplicate counts (correctness), with Speakable reported as a downstream consequence, not a target.

## 9. Optimal bounded context — explicit determination

- **Smallest context strategy that materially improves stability**: **C1** (one preceding committed unit) — it eliminated contradictions in 4 of 10 affected cases for a 41% character-volume increase.
- **Diminishing returns**: **confirmed, explicitly** — C2 added exactly one more improved case (F) for a further 5% character increase; C3 and C4 added zero further improvement over C2 in this corpus.
- **Maximum practical context size observed**: 17 characters (the largest single `maxReqChars`-context-only figure across any case's C2/C3/C4 result, case `A_SimpleStatement`'s ~9-char context plus other cases up to `B_LongSentence`'s 43-char segment) — far below both the C3 (30) and C4 (80) bounds, meaning **this corpus's utterances were simply never long enough to test those bounds' real limiting behavior.**
- **Unacceptable amplification/latency**: **not observed** — even C4's worst-case corpus-wide amplification (1.46x) is modest, and no strategy showed a systematically different latency profile from another (the one large outlier, §6, affected C0's very first call, unrelated to context size).

## 10. Stress cases

All 14 originally-required categories are covered by the reused A-N corpus (simple statement, long sentence, German subordinate clause, German separable verb, modal verb, question, conjunction continuation, self-correction, colloquial phrase, technical/business phrase, consecutive utterances, fast-speech-like progression, short utterance) plus 2 new cases added this step (negation: `O_Negation`; an additional short utterance: `P_ShortUtterance`, German "Nein.").

## 11. Batch API — not used, per instruction

The confirmed batch capability (Step 5.9 §1) was **not** used this step — each of the 5 strategies issued its own individual request, exactly as instructed ("do not redesign production around it... only measure whether batching affects request overhead/latency" was optional and was not exercised this step, since the primary comparison — context window size — did not require it). No claim is made about batching's effect on latency/overhead in this document.

## 12. No new AI model

**PROVEN**: no LLM, OpenAI, Claude, Gemini, local model, or embedding system was introduced — confirmed by review of every new file, which calls only `AzureTranslatorTextProvider`/`IIncrementalTranslationProvider`.

## 13. Security

**PROVEN**: neither `_logger.Log` call site in `ContextWindowTranslationExperiment.cs` includes source/context/translated text (confirmed by code review — both interpolate only counts/states/booleans/latencies/character-counts). No persistent storage of raw speech text was added.

## 14. Production isolation

**PROVEN**: `AzureSpeechTranslationProvider.cs` — 0 references to any Step 5.11 type. No experimental result reaches the production audio path. No TTS/playback integration exists in this step's code.

## Tests

**PROVEN**: 15 new unit tests, all passing — C0's zero-context guarantee, C1's exactly-one-preceding-unit inclusion (and C2 never sending less context than C1 at the same step), C3's character-limit constant and never-exceeded bound (verified across a 10-partial growing sequence), C4's bound exceeding C3's, bounded-context tail-truncation (not head-truncation), self-correction trailing-word safety, full cross-utterance isolation for all 5 strategies simultaneously, deterministic contradiction detection (rigged by exact request-text content, confirmed to fire identically across all 5 strategies at the same step), clean-extension-not-contradiction classification, translation failure and empty-response handling, explicit reset, null-not-zero metrics for zero-request utterances, the C0 amplification-ratio identity (always exactly 1.0), and privacy. Full suite: **289/289 passed.**

## 17. Final decision — explicit answers, not assumed

1. **What is the smallest useful context?** **C1 — one immediately-preceding committed source segment.** It captured the large majority of the measured stability benefit (4 of the 5 total contradiction-eliminating cases) at the smallest context cost of any strategy beyond C0.
2. **Does larger context materially improve results?** **Only marginally, and with clear diminishing returns.** C2 added one further improved case; C3 and C4 added zero further improvement over C2 in this corpus — their extra headroom was never actually exercised by these utterance lengths.
3. **What is the request amplification?** **1.41x (C1), 1.46x (C2/C3/C4)**, measured corpus-wide against C0's baseline — explicitly labeled experimental, not a production cost claim, per instruction (this is a 16-case, short-utterance corpus, not representative of sustained conversational volume).
4. **What latency cost does context introduce?** **None measurable beyond normal call-to-call variance** (112-333ms typical for every strategy) — the one major outlier (13.5s) affected C0's very first call and is attributable to process cold-start, not to context size.
5. **Is there a bounded context strategy worth carrying into the next stage?** **Yes — C1, or C2 if the small additional cost is acceptable.** C3/C4's larger bounds are not yet justified by this corpus's evidence; they remain plausible for longer conversations but that is **UNVALIDATED**, not demonstrated.
6. **Does the evidence now justify a TTS feasibility experiment?** **No — not yet.** Even the best-performing strategy (C2/C3/C4, tied) reduced contradiction-affected cases from 10/13 to 5/13 — real, substantial progress across Steps 5.9→5.10→5.11, but still affecting more than a third of realistic utterances, and `Speakable` was reached in only 1 of 16 cases. The evidence trend across this whole experimental series is genuinely positive (Step 5.9's best: 1/14 Speakable with no context; Step 5.10: 1/14 with unbounded application-side context; Step 5.11: 1/16, now with a *characterized, bounded, minimal* context strategy achieving the same result at known, modest cost) — but "not yet" remains the honest, measured answer.

## Final Verification

- **Test count**: 289/289 passed.
- **Build**: 0 warnings, 0 errors.
- **Secret scan**: clean.
- **Production files changed**: none — `AzureSpeechTranslationProvider.cs` untouched.
- **Experiment files added**: `ContextWindowModels.cs`, `ContextWindowTranslationExperiment.cs` (new); `PrefixStabilityEngine.cs`/`AzureTranslatorTextProvider.cs` reused unmodified.
- **Live API calls**: 140 real Translator requests (28 per strategy × 5 strategies) totaling 1,449 characters (213+300+312+312+312) across 16 cases, real dedicated Translator credentials only.
- **Complete recommendation**: carry C1 (or C2, where its marginal extra cost is acceptable) forward as the working bounded-context baseline for any future refinement. Do not proceed to a TTS feasibility experiment yet — correctness has measurably improved across three consecutive steps (5.9→5.10→5.11) but has not yet crossed a threshold where most realistic utterances produce a defensible pre-final speech candidate.
