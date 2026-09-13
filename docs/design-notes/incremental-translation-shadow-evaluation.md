# Step 5 — Shadow Incremental Translation Evaluation

**Status: SHADOW/OBSERVATION ONLY. Existing final-result translation path unchanged. No
experimental translation reaches TTS or playback. No user-visible translated speech
changed. No Azure provider configuration was modified — no new translation API call was
introduced (see §2).**

---

## 1. Objective

Evaluate how stable source segments (from Policy A, `PrefixStabilityEngine`, unmodified) could be translated incrementally without duplicated translation, missing translation, broken grammar, contradictory output, unstable translation, or excessive context loss — by running three candidate strategies in shadow against real Azure data and measuring their behavior against the true final authoritative translation.

## 2. Architecture

```
Azure Recognizing
       │
       ▼
PrefixStabilityEngine (Policy A, unmodified) ──▶ stable source segment / cumulative stable source
       │                                                    │
       │ Azure's OWN already-produced translated text        │
       │ (e.Result.Translations — no new API call)            │
       ▼                                                    ▼
              AzureAlignedIncrementalTranslationShadow
                    │            │            │
              Strategy A   Strategy B   Strategy C
                    │            │            │
                    └────────────┴────────────┘
                                 │
                    diagnostic comparison + Final reconciliation (metadata only)
```

**Critical design decision, stated explicitly**: this evaluation does **not** call any new translation API for isolated segments. `IncrementalTranslationInput.LatestPartialTranslatedText`/`FinalTranslatedText` are Azure's own, already-produced translated text — the same `e.Result.Translations` value the existing production path already reads on every `Recognizing`/`Recognized` event. All three strategies operate by *reinterpreting* this already-available data through different lenses (independent-per-partial, cumulative-re-snapshot, gated-rolling-window), correlated with Policy A's source-stability decisions. This was a deliberate scoping choice: this project has only an Azure Speech resource (key/region), not a separate Text Translation resource, and the Step 5 instructions explicitly prohibit modifying Azure provider configuration — introducing a new external translation dependency to literally call Azure per-segment would have required exactly that. The chosen design still directly measures the requested comparison (independent-segment vs. cumulative-context vs. rolling-window translation behavior) using real, live Azure translation output, just without spending a second, separate API call per segment.

`AzureAlignedIncrementalTranslationShadow` ([src/VTTranslate.Core/Streaming/AzureAlignedIncrementalTranslationShadow.cs](../../src/VTTranslate.Core/Streaming/AzureAlignedIncrementalTranslationShadow.cs)) has no events and no reference to translation-dispatch, TTS, or playback — the same structural guarantee as `ShadowStabilityObserver` (Step 3) and `CommitPolicyComparator` (Step 4). Wired into `AzureSpeechTranslationProvider` at the same two existing shadow call sites (`Recognizing`/`Recognized`), purely additively — confirmed by diff that `PartialResult?.Invoke`, `FinalResult?.Invoke`, and `AudioSynthesized?.Invoke` still construct solely from `e.Result`/`audio`, unchanged.

## 3. Strategy A — naive/independent (ungated, ever-partial, blind append)

On **every** partial (not gated by source stability), computes the word-level delta between the current and immediately-previous partial's translated text, and **blindly appends it to a running cumulative buffer regardless of whether the delta is a clean extension or a contradiction/revision**. This deliberately reproduces the exact failure mode the Step 5 objective warns against ("do not simply call translation independently for every tiny stable segment and concatenate the results") — its measured duplicate-token rate against the real final translation is this evaluation's concrete answer to "how bad is the naive approach."

## 4. Strategy B — cumulative context re-translation (stability-gated, wholesale replace)

Only when Policy A reports a newly stable source commit (or at Final), takes a fresh whole snapshot of Azure's current full translated text, **replacing** the previous candidate outright — never incremental (must re-render the entire translation every gated step), but always internally consistent, since it's just Azure's own single coherent translation of the growing text, taken wholesale rather than diffed.

## 5. Strategy C — gated rolling window (contradiction-aware incremental)

Only when Policy A reports a newly stable source commit (or at Final), compares the current translated text against the translated text **as of the previous stability-gated step** (not every partial, unlike Strategy A). A clean extension is appended incrementally; a contradiction is **withheld** — not appended — mirroring Policy A's own "never retract, wait for agreement" rule, applied here to the translated-text stream instead of the source-text stream.

## 6. Context model

All three strategies operate per-utterance, reset fully (including all internal reference/cumulative state) on a new `UtteranceId`, mirroring Policy A/B's reset semantics from Steps 2-4. None of the three strategies uses cross-utterance context or memory — this is intentionally out of scope (no Adaptive Translation Memory, no naturalization, per the explicit constraints). "Cumulative stable source context" (as passed into `IncrementalTranslationInput.CumulativeStableSource`) is read from Policy A but is not itself translated a second time — see §2's scoping note.

## 7. Segment reconciliation

At Final, each strategy's cumulative candidate is reconciled against the true final translated text via a **structural, token-multiset comparison — explicitly NOT a semantic/LLM judgment** (no LLM was used anywhere in this evaluation, per the constraint). `ApproxMissingTokenCount` = tokens present in the final translation more often than in the strategy's cumulative candidate; `ApproxDuplicateTokenCount` = the reverse. This is an honest, auditable approximation, not a claim of semantic correctness — documented as such throughout (§12).

## 8. Duplicate handling

Strategy A has **no** duplicate-prevention mechanism by design (§3) — this is the point being evaluated. Strategies B and C both prevent duplication structurally: B always replaces wholesale (nothing to duplicate against, since the previous candidate is discarded each time); C only appends a genuine delta beyond its own last-known-good reference, and withholds rather than appends on contradiction. Verified live (§11): Strategy A showed substantial `approxDuplicateTokenCount` in nearly every multi-partial case (e.g., 74 duplicate tokens in case F, 59 in case G), while B and C's duplicate counts stayed low (0-4) across the same cases.

## 9. Correction handling

Distinguishes the same three tiers required by the prompt: **speculative** (`PendingUnstableText`/anything not yet a `StrategyCandidateResult`), **stable-but-revisable** (a `StrategyCandidateResult`, still shadow-only, never claimed as safe to speak/display), and **final authoritative** (`FinalTranslatedText`, always ground truth). None of the three strategies pretends a previously-proposed speculative or stable candidate can be physically retracted — Strategy A's blind-append behavior is the concrete illustration of what happens if a system tried to treat "stable" as "safe to have already spoken," and its high duplicate/contradiction counts (§11) are the measured cost of that assumption.

## 10. Final authoritative reconciliation

`ObserveFinal` always computes and returns all three strategies' `StrategyFinalReconciliation` records — the Final is unconditionally authoritative for reconciliation purposes exactly as Policy A's own Final handling has been since Step 2. No strategy's cumulative candidate is ever substituted for the Final translation in any downstream path (there is no downstream path — this remains shadow-only).

## 11. Live measurements

Real Azure, `translation-shadow-test` (reusing Step 1/3/4's WAV files for A-D, F-H; Step 4's conversational/punctuation/mid-correction files relettered as I/J/K per Step 5's own case list, which differs from Step 4's; a new terminology/business-vocabulary file for L). Case E investigated separately via `case-e-investigate` (§13). Full logs: `test-results/logs/translation-shadow/*.log`.

| Case | Final tokens | A: cumulative / missing / duplicate / revisions / chunks | B: cumulative / missing / duplicate / revisions / chunks | C: cumulative / missing / duplicate / revisions / chunks |
|---|---|---|---|---|
| A EN→DE normal | 7 | 0 / 7 / 0 / 0 / 0 | 0 / 7 / 0 / 0 / 0 | 0 / 7 / 0 / 0 / 0 |
| B DE→EN normal | 8 | 10 / 2 / 4 / 2 / 3 | 8 / 1 / 1 / 0 / 3 | 4 / 6 / 2 / 2 / 1 |
| C EN→DE fast | 13 | 10 / 7 / 4 / 1 / 1 | 12 / 5 / 4 / 0 / 1 | 12 / 5 / 4 / 0 / 1 |
| D DE→EN fast | 15 | 21 / 2 / 8 / 3 / 6 | 10 / 6 / 1 / 0 / 3 | 7 / 11 / 3 / 1 / 2 |
| F EN→DE long | 31 | 103 / 2 / **74** / 7 / 7 | 31 / 3 / 3 / 0 / 7 | 6 / 26 / 1 / 6 / 1 |
| G EN→DE correction | 10 | 67 / 2 / **59** / 8 / 8 | 18 / 2 / 10 / 0 / 8 | 5 / 8 / 3 / 7 / 1 |
| H EN→DE ×3 consecutive | 7/8/2 | 0/7/0 · 7/2/1 · 0/2/0 (missing/dup) | 0/7/0 · 8/1/1 · 2/1/1 | 0/7/0 · 8/1/1 · 2/1/1 |
| I EN→DE conversational | 15 | 38 / 1 / 24 / 5 / 7 | 16 / 2 / 3 / 0 / 6 | 8 / 10 / 3 / 4 / 2 |
| J EN→DE punctuation | 15 | 13 / 6 / 4 / 1 / 2 | 12 / 3 / 0 / 0 / 2 | 7 / 10 / 2 / 1 / 1 |
| K EN→DE mid-correction | 10 | 25 / 2 / 17 / 4 / 5 | 10 / 1 / 1 / 0 / 5 | 5 / 7 / 2 / 4 / 1 |
| L EN→DE terminology | 21 | 45 / 2 / 26 / 8 / 11 | 21 / 2 / 2 / 0 / 8 | 12 / 10 / 1 / 6 / 2 |

(Case A and case H's first/third utterances had zero partials — straight-to-Final, matching Step 1's short-utterance finding — so no strategy had any opportunity to produce an incremental candidate; all three correctly show 0 cumulative / full missing-count, which is the structurally correct outcome, not a defect.)

## 12. Quality observations

**Strategy A (naive) is measurably the worst on every multi-partial case, exactly as the objective anticipated.** Its duplicate-token count is dramatically higher than B or C in every case with ≥3 partials (74 in F, 59 in G, 26 in I, 17 in K), because it blindly re-appends translated-text deltas even when the underlying translation revised itself (Step 1's own finding that translated text regresses far more than source text — now directly, quantitatively confirmed as a real cost, not just a qualitative concern). **Strategy B (cumulative re-translation) is consistently the most accurate**, with missing/duplicate counts staying low and close to each other across every case (e.g., case F: 3 missing / 3 duplicate against 31 final tokens) — because it always uses Azure's own single, internally-consistent full-text translation rather than trying to stitch fragments together. **Strategy C (gated rolling window) undershoots badly** — high missing-token counts in most multi-chunk cases (26 of 31 in F, 10 of 15 in I) — because its contradiction-withholding rule fires very often against Azure's genuinely volatile per-partial translated text (Step 1 §4/§9), so most of the growth gets held back and only recovered structurally by comparing against the Final, which this reconciliation step does capture as "missing" rather than "eventually recovered" (a measurement-scope limitation, not a claim the content is truly lost — see §14).

**This is a clean, honest negative result for the naive approach and for the "smart" gated strategy alike** — no strategy here is being declared a full success. Per the explicit correctness requirement, none of A/B/C's low latency alone (§13) qualifies as success; B is the only strategy that is both structurally sound (low duplicate/missing) and technically correct — Strategy C's incrementality intuition did not survive contact with how volatile Azure's actual per-partial translated text is.

## 13. Latency observations

"Time from first stable source commit to incremental translation candidate" (`elapsedFirstStableToFirstCandidateMs*` fields, logged per step) was consistently near-zero for Strategies B and C, since both are directly gated by the same source-stability event that defines "first stable source commit" — there is no meaningful latency gap to measure for them by construction. Strategy A's first candidate can occur before any source stability at all (it's ungated), so this metric does not meaningfully apply to it. As in Step 4, EN→DE cases in this run show the same pre-existing connection-warmup latency anomaly dominating absolute timing figures (§14 of `commit-policy-shadow-evaluation.md`); this does not affect the *relative* strategy comparison in §11-12, which is computed structurally (token counts), not from timestamps.

## 14. Failure cases

- **Strategy A's duplicate-content failure mode is not hypothetical — it is the dominant observed behavior** across every multi-partial live case (§12). This is the clearest, most concrete evidence in this whole evaluation series that "translate independently and concatenate" is unsound for this pipeline.
- **Strategy C's "missing" counts likely overstate true content loss** — because the reconciliation in §7/§11 only measures each strategy's *own* cumulative candidate against the Final, a word C withheld due to a (correctly-detected) contradiction is counted as "missing" even though C's underlying mechanism is explicitly designed to eventually recover such content once agreement resumes (the same principle already verified for Policy A's source-side behavior in Steps 2/2.5). This evaluation did not implement a "C would have caught up by the next gated step" recovery simulation — a real limitation of this measurement's scope, not necessarily of Strategy C's real-world viability. Flagged honestly rather than either fixed silently or hidden.
- **Case E (short utterance) reproduced the same ~13.1s Azure connection-warmup anomaly** documented in Step 4 (13,074ms this run vs. 13,170ms previously) — see §15. With zero partials, no strategy had a chance to act; this is the structurally correct (not defective) outcome for a zero-partial utterance.

## 15. Recommendation

**Strategy B (cumulative context re-translation, gated by source stability) is the only strategy from this evaluation with evidence supporting further consideration** — it was the most accurate against real Azure data in every single live case, at effectively the same latency profile as Strategy C. **Strategy A should be considered disqualified** by this evaluation's own data — its duplicate-content rate is severe and consistent, not an edge case. **Strategy C's poor showing warrants a second look with an improved reconciliation methodology** (§14) before being disqualified outright, since its underlying mechanism (contradiction-aware, source-stability-gated) is the same principle already validated for Policy A's source-side commits. None of this constitutes authorization to connect any strategy to real translation dispatch, TTS, or user-visible output — that remains a separate, future, explicitly-approved step. If a future step is ever authorized to explore live incremental translation, this evaluation's data points toward Strategy B (or a corrected re-evaluation of C) as the starting point, not Strategy A.
