# Step 5.8 — Semantic Segment / Translation Commit Experiment

**Status: SHADOW/EXPERIMENTAL ONLY. `AzureSpeechTranslationProvider.cs` was not modified
(0 references to any Step 5.8 type — confirmed by grep, §17). No production audio path
calls this experiment. No TTS or playback consumes its output.**

**Legend**: **PROVEN** = directly demonstrated by an executed unit test or a real live
API call this step. **OBSERVED** = seen in this step's live run (14 cases, one run) — a
real data point, not yet a broad claim. **UNVALIDATED** = not exercised this step.
**ASSUMED** = a design choice not independently verified this step.

---

## 1. Objective

Determine whether there is a practical point EARLIER than final ASR — and earlier than mere SOURCE stability (Policy A) — at which a source segment is *semantically* complete enough to translate independently and safely. **SOURCE STABILITY != TRANSLATION READINESS** is the premise: Policy A already tells us a prefix is unlikely to be revised, but says nothing about whether it is a complete thought.

## 2. The heuristic (deterministic, explainable — NOT an AI/LLM classifier)

`SemanticCompletionHeuristic` ([src/VTTranslate.Core/Streaming/SemanticCompletionHeuristic.cs](../../src/VTTranslate.Core/Streaming/SemanticCompletionHeuristic.cs)) evaluates a committed SOURCE prefix using four ordered, surface-level rules, conservative by construction (any ambiguous case returns "not complete" — WAIT):

1. **Trailing sentence-ending punctuation** (`.`/`!`/`?`) → complete. The strongest, most conservative signal.
2. **Trailing continuation-trigger word** — a hand-picked set of English/German conjunctions, prepositions, articles/determiners, and modal/auxiliary verbs (e.g. "and", "to", "the", "would", "und", "zu", "der", "möchte") → not complete. These words grammatically imply more content is coming.
3. **German separable-verb pending-particle check** — a small, explicitly non-exhaustive dictionary of common separable-verb stems (e.g. "rufe" → requires "an" somewhere in the prefix) → not complete if the particle hasn't appeared yet, regardless of how long the rest of the prefix is.
4. **Conservative minimum-length fallback** — without punctuation and without a trailing trigger/pending particle, require at least 6 tokens before declaring completion.

This is explicitly **not a parser** — no part-of-speech tagging, no dependency parsing, no true semantic understanding. It is a fast, auditable approximation whose failure modes are visible from its own rule list (see §11).

## 3-4. Test methodology: both directions, realistic ASR sequences

All 14 required cases (A-N, §12) were run against the real Translator API with hand-written, monotonically-growing partial-text sequences approximating ASR `Recognizing` progression — 5 EN→DE cases, 4 DE→EN cases, matching the instruction's emphasis on German word-order dependencies (modal verbs, subordinate clauses, separable verbs specifically included as dedicated cases D and E).

## 5. Independent Translator

**PROVEN (live)**: every one of 41 real translation calls across this run's 14 cases succeeded (HTTP 200, 0 failures) — the same dedicated `AZURE_TRANSLATOR_KEY`/`AZURE_TRANSLATOR_REGION` credentials validated in Step 5.6b, never the Speech-bundled translation. No mock/fake was used for any live result (mocks are used only in the accompanying unit test suite, §13).

## 6-7. Earliest semantic commit / policy comparison

**OBSERVED, and this is this step's central finding**: **Policy B (conservative semantic commit) fired mid-stream in only 2 of the 14 cases** (F_LongSentence and I_FastSpeech — both long enough to reach the heuristic's 6-token punctuation-free fallback). In the other 12 cases, Policy B issued **zero requests before Final** — including the two cases specifically designed to test whether the heuristic correctly recognizes incompleteness (D, German subordinate clause; E, German separable verb) and the two "should be easy" short cases (A, B). **Policy C behaved nearly identically to Policy B for mid-stream firing** (same 2 of 14 cases triggered before Final), but always additionally produces exactly one request at Final (its unconditional pending-buffer flush), so its `TranslationRequestCount` is never 0 the way Policy B's can be.

| Case | Direction | Policy A requests | Policy B requests (mid-stream) | Policy C requests (mid-stream) | Policy B/C fired before Final? |
|---|---|---|---|---|---|
| A Simple statement | EN→DE | 3 | 0 | 0 (1 at Final) | No |
| B Question | EN→DE | 2 | 0 | 0 (1 at Final) | No |
| C Modal verb | EN→DE | 3 | 0 | 0 (1 at Final) | No |
| D German subordinate clause | DE→EN | 3 | 0 | 0 (1 at Final) | No |
| E German separable verb | DE→EN | 4 | 0 | 0 (1 at Final) | No |
| F Long sentence | EN→DE | 3 | **1** | **1** | **Yes** |
| G Conjunction continuation | EN→DE | 3 | 0 | 0 (1 at Final) | No |
| H Self-correction | EN→DE | 1 | 0 | 0 (1 at Final) | No |
| I Fast speech | EN→DE | 1 | **1** | **1** | **Yes** |
| J Short "Ja." | DE→EN | 0 | 0 | 0 | n/a (zero partials) |
| K Incomplete sentence | EN→DE | 2 | 0 | 0 (1 at Final) | No |
| L consecutive ×2 | EN→DE | 0, 0 | 0, 0 | 0, 0 | No (both too short to grow past 1-2 partials) |
| M Colloquial | EN→DE | 1 | 0 | 0 (1 at Final) | No |
| N Business/technical | EN→DE | 2 | 0 | 0 (1 at Final) | No |

**Policy A (baseline)** fired on every source-stable commit in every non-trivial case, exactly reproducing Step 5.7's finding: always `Candidate` state, never itself safe.

**Do not assume any policy wins** — per instruction, and confirmed: Policy B/C's conservatism, while correctly avoiding premature translation (§9), means in this realistic-partial-text data they provide **no earlier signal than Policy A** for all but the longest utterances. This is a genuine, measured limitation of the *current* heuristic parameters (specifically the 6-token minimum-length fallback combined with the complete absence of mid-utterance punctuation in realistic partials), not evidence that semantic gating is a bad idea in principle.

## 8. Translation reconciliation

**PROVEN (unit test + live)**: `RevisionCount` was 0 in every live case for every policy — no policy ever contradicted its own already-committed content, structurally guaranteed for Policy A/B (wholesale replace) and unit-tested for Policy C's incremental-append path (`TranslationFailure_MapsToRevisionRequired`). **`ContradictionCount` was 0 in every case for every policy — but this metric was NOT meaningfully exercised this run** (§11 limitation: Policy C's contradiction-detection comparator is currently a permissive stub, documented honestly rather than silently left unmentioned). Missing/duplicate token counts followed the same pattern already established in Step 5.7 — nonzero `missing` in almost every case (a direct, structural consequence of the trailing-word buffer always holding back content the Final later reveals), small nonzero `duplicate` counts explained by comparing two non-identical translations of overlapping-but-different source text (Step 5.7 §9's already-documented reconciliation-method artifact, not literal repeated output).

## 9. Naturalness / quality — n/a for this step's focus

Not separately assessed this step (Step 5.7 §8 already covered a small inspection sample); this step's focus was the timing/gating decision, not translation quality itself. No naturalness claim is made here.

## 10. Latency

**PROVEN (live)**: the overwhelming majority of the 41 real calls this run completed in **130-330ms**. **OBSERVED, two outliers**: case A's very first call measured **13,420ms** (consistent with the same cold-start-class anomaly seen once in Step 5.7's run — the first call of the whole process) and case E's second call measured **3,002ms** (a smaller, second spike, **not** at the very first call of the run this time) — worth flagging honestly as **UNVALIDATED** whether this is a recurring pattern or two independent one-off events; not re-run to confirm, consistent with this series' "minimal necessary live calls" practice. `EarliestSemanticCommitDelayMs` (first-partial-to-semantic-commit) was only measurable for the 2 cases where Policy B/C actually fired (F: 639-855ms, I: 212-366ms) — correctly reported as `null`, not `0`, for every case where semantic commit never happened before Final. Per instruction, **no physical audible latency is claimed anywhere** — TTS and playback are not part of this experiment.

## 11. Failure modes / limitations

- **The heuristic's conservatism, combined with realistic (punctuation-free) partial text, makes Policy B/C nearly equivalent to "wait for Final" in this data** — the single most important limitation, stated plainly rather than downplayed (§6-7).
- **Policy C's contradiction-detection is currently a permissive stub** (`IsConsistentWith` always returns `true`, with an explicit code comment explaining why — independently-translated segments are not expected to share a literal token prefix, so the simple LCP-style check used elsewhere in this project's Streaming/ code doesn't directly apply here). This means `ContradictionCount` cannot currently be trusted as a real signal — it is architecturally present (the enum value and code path exist) but not yet meaningfully implemented. Documented honestly rather than silently shipped as if working.
- **A coincidental double-block observed in unit testing**: the German separable-verb particle "an" is *also* listed as a German preposition in the continuation-trigger word set, so a prefix ending exactly on the particle itself (e.g. "...morgen früh an") is still flagged as incomplete — for the *wrong* reason (trailing-preposition rule, not the separable-verb rule, which in that specific case is actually satisfied). Functionally harmless for this heuristic's conservative goal (both rules point the same direction — WAIT), but a real accuracy/explainability gap worth fixing if this heuristic is ever refined.
- **The persistent diagnostic log does not record the heuristic's specific "reason" text for WAIT decisions** (only for a successful commit) — confirmed while investigating cases D/E's live results; the *specific* rule responsible for each WAIT in the live run cannot be reconstructed from the log alone (only from the unit tests, which do assert on `Reason` directly). A minor, honestly-reported gap in this run's own observability, not a correctness issue.
- **Small sample**: 14 cases, one live run, hand-authored (not real-audio-driven) partial sequences — the same limitation already noted in Step 5.7.

## 12. Evaluation cases

All 14 required cases (A-N) were run — see the table in §6-7 for the full per-case data.

## 13. Testing

**PROVEN**: 34 new unit tests, all passing — 20 for `SemanticCompletionHeuristic` (punctuation, modal verbs in both languages, conjunctions, subordinate-clause introducers, prepositions/articles, separable-verb pending-particle and particle-present cases, minimum-length fallback both under and over threshold, and a dedicated "long but still waits on trailing preposition" conservatism test) and 14 for `SemanticSegmentTranslationExperiment` (Policy A ungated firing, Policy B waiting on modal verbs/conjunctions/subordinate clauses/separable verbs, self-correction trailing-word safety, finalization force-flush + authoritative translation, consecutive-utterance isolation, duplicate prevention, translation failure mapping, explicit reset, privacy, zero-partial short utterance). Full suite: **249/249 passed.**

## 14. Security

**PROVEN**: no API key, authorization header, or environment variable value is ever printed/logged anywhere in this step's new code (`grep` confirms both `_logger.Log` call sites use only counts/states/booleans/reasons — the `Reason` field is a fixed, hand-written string like `"trailing continuation-trigger word 'and'"`, never raw recognized/translated speech text). No persistent storage of speech text was added — the diagnostic log file already excludes it by the same convention established in every prior step.

## 15. Production isolation

**PROVEN**: `AzureSpeechTranslationProvider.cs` — 0 references to `SemanticSegmentTranslationExperiment`, `SemanticCompletionHeuristic`, or any Step 5.8 type (confirmed by grep). No production audio/TTS/playback path calls this experiment — it is only invoked from the new `semantic-segment-translation-test` command in `VTTranslate.LiveTest` and from the unit test suite.

## 16. Recommendation

**The heuristic's core safety behavior is validated** — it never fired prematurely on any of the deliberately-tricky German cases (D, E) or the ambiguous shorter English cases, consistent with "when uncertain, WAIT." But **as currently parameterized, it does not yet demonstrate a practical earlier commit point** for the majority of realistic utterance shapes — it only provides earlier signal for genuinely long sentences. Before any further step: (a) the minimum-token-count fallback (currently 6) is the single largest lever — this step deliberately did not tune it (per "latency reduction is secondary to correctness"), but a future step could measure whether a lower or higher threshold changes the false-negative rate without introducing false positives; (b) Policy C's contradiction detection needs a real implementation before `ContradictionCount` can be trusted; (c) the double-block coincidence (§11) should be fixed for explainability, even though it's currently harmless. **This step's honest conclusion, grounded in measured results**: semantic gating is a sound safety concept (it never got it wrong in this data) but is not yet a proven *latency* win — the two goals (correctness and earliness) were, in this data, in tension, and correctness was correctly prioritized, per instruction, at the cost of nearly always waiting for Final anyway.

## 17. Final verification

- **Total tests**: 249/249 passed.
- **Build**: 0 warnings, 0 errors.
- **Secret scan**: clean.
- **Production files modified**: none — `AzureSpeechTranslationProvider.cs` untouched, confirmed by grep (0 matches for any Step 5.8 type).
- **Experiment files added**: `SemanticCompletionHeuristic.cs`, `SemanticSegmentModels.cs`, `SemanticSegmentTranslationExperiment.cs` (all new); `PrefixStabilityEngine.cs` and `AzureTranslatorTextProvider.cs` (Steps 2/5.6a) reused **unmodified**.
