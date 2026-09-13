# Step 5.7 — Streaming Translation Decision Experiment

**Status: SHADOW/EXPERIMENTAL ONLY. `AzureSpeechTranslationProvider.cs` was not modified
(confirmed: zero references to any Step 5.7 type inside it — see §16). No production
audio path calls this experiment. No TTS or playback consumes its output.**

**Legend used throughout this document**: **PROVEN** = directly demonstrated by an
executed unit test or a real live API call in this step. **OBSERVED** = seen in this
step's live run, but from a limited sample (14 cases, single run) — a real data point,
not yet a broad claim. **UNVALIDATED** = not tested in this step, status unknown.
**ASSUMED** = a design choice made without independent verification this step, carried
over from an earlier step's evidence.

---

## 1. Architecture

```
StreamingSourceObservation (realistic, growing ASR-style partials)
        │
        ▼
PrefixStabilityEngine (Policy A, UNMODIFIED — reused directly, not duplicated)
        │  produces: SourceCommitState (Provisional / Stable / Final)
        │            + NewlyCommittedSegment / CommittedSourceText
        ▼
StreamingTranslationDecisionExperiment (NEW, this step)
        │  gates on SourceCommitState == Stable before issuing ANY translation request
        ▼
IncrementalTranslationProviderExperiment (Step 5.5/5.6b, UNMODIFIED — reused directly)
        │  runs B1 (cumulative) and B3 (boundary-aware); B2 excluded from reporting
        │  per instruction (known duplicate-inflation problem, not a primary candidate)
        ▼
IIncrementalTranslationProvider → AzureTranslatorTextProvider (UNMODIFIED — reused directly)
        │  the genuine, independent Azure Translator Text API — dedicated
        │  AZURE_TRANSLATOR_KEY/AZURE_TRANSLATOR_REGION/AZURE_TRANSLATOR_ENDPOINT only
        ▼
        produces: TranslationCommitState (Candidate / StableCandidate / RevisionRequired / Authoritative)
```

New files this step: [StreamingTranslationModels.cs](../../src/VTTranslate.Core/Streaming/StreamingTranslationModels.cs) (the two state enums + observation/result records) and [StreamingTranslationDecisionExperiment.cs](../../src/VTTranslate.Core/Streaming/StreamingTranslationDecisionExperiment.cs) (the orchestrator). Nothing else in `src/VTTranslate.Core/Streaming/` was modified — `PrefixStabilityEngine.cs`, `IncrementalTranslationProviderExperiment.cs`, and `AzureTranslatorTextProvider.cs` are all reused exactly as built in earlier steps, satisfying "do not duplicate existing logic unnecessarily."

## 2. Experiment methodology

Unlike Steps 5.5/5.6b's synthetic 2-segment splits, this step drives the experiment with **realistic, multi-partial, monotonically-growing text sequences** approximating what an ASR `Recognizing` stream looks like (5-9 partials per case for longer sentences), through the real, unmodified Policy A engine — so the SOURCE-side commit timing in this step's results reflects genuine `PrefixStabilityEngine` behavior (trailing-word buffer, contradiction handling), not a hand-picked 2-step split. Deliberately, per Step 1's own finding that real Azure partials rarely carry mid-utterance punctuation, none of these hand-written partial sequences include punctuation until the Final — this choice was not made to bias the result; it is what real ASR partial text plausibly looks like, and its consequence for B3 is one of this step's central findings (§8).

## 3. B1 results (cumulative)

**PROVEN (live)**: B1 fired on every single `Stable` source commit across all 12 non-zero-partial cases (34 total translation requests across the run), always with `TranslationCommitState.Candidate` (by design — see §4) and always `CallSucceeded=true` (0 failures across 34 calls). **OBSERVED**: `ApproxMissingTokenCount` was non-zero in every case that reached Final with committed content (range 2-8 tokens) — B1's last partial-time cumulative translation is always a translation of a still-incomplete source prefix (the trailing-word buffer always holds back the final word(s)), so it necessarily omits whatever the Final adds. `ApproxDuplicateTokenCount` was small but non-zero in most cases (0-2 tokens) even though B1 never contradicts itself by design (§7) — see §9 for why this is a token-reconciliation-method artifact, not a "spoken twice" defect.

## 4. B3 results (boundary-aware)

**OBSERVED, and this is this step's most important finding**: **B3 never produced a mid-stream candidate in any of the 14 test cases.** Its `TranslationRequestCount` in the Final summary was always exactly 1 — the pending-buffer flush that happens unconditionally at Final (§6 of `incremental-translation-provider-feasibility.md`), never an actual sentence-boundary-triggered mid-utterance translation. Its reconciliation numbers (`missing`/`duplicate`) were therefore identical to B1's in every case, because both ultimately compare a single translated blob against the Final. **Do not assume B3 is correct simply because Step 5.6b showed good reconciliation** — Step 5.6b's cases were built from only 2 hand-picked segments that happened to include a period in the second one; this step's more realistic, punctuation-free partial progression shows that B3, as currently implemented (gated on literal sentence-ending punctuation appearing inside an already-committed SOURCE segment), provides **zero measured incremental benefit** over simply waiting for the Final, when fed realistic ASR-style partial text. This is a real limitation of the *implementation*, not necessarily of the *boundary-aware concept* — see §11 recommendation.

## 5. All test cases (A-L, live)

| Case | Direction | Partials | B1 requests | B1 missing/dup | B3 requests | B3 missing/dup | B3 fired mid-stream? |
|---|---|---|---|---|---|---|---|
| A Normal speech | EN→DE | 8 | 6 | 3/1 | 1 | 3/1 | No |
| B Fast speech | EN→DE | 3 | 2 | 8/1 | 1 | 8/1 | No |
| C Self-correction | EN→DE | 3 | 2 | 5/1 | 1 | 5/1 | No |
| D Long sentence | EN→DE | 9 | 7 | 5/0 | 1 | 5/0 | No |
| E Short utterance | DE→EN | 0 (straight to Final) | 0 | null/null | 0 | null/null | n/a |
| F German | DE→EN | 6 | 4 | 3/1 | 1 | 3/1 | No |
| G English | EN→DE | 6 | 4 | 4/1 | 1 | 4/1 | No |
| H Colloquial | EN→DE | 6 | 4 | 7/2 | 1 | 7/2 | No |
| I Incomplete | EN→DE | 4 | 3 | 3/0 | 1 | 3/0 | No |
| J Repeated/corrected | EN→DE | 4 | 3 | 4/1 | 1 | 4/1 | No |
| K utterance 1 | EN→DE | 2 (no growth beyond period) | 0 | null/null | 0 | null/null | n/a |
| K utterance 2 | EN→DE | 3 | 1 | 2/1 | 1 | 2/1 | No |
| L Speaker A | EN→DE | 3 | 2 | 5/1 | 1 | 5/1 | No |
| L Speaker B | DE→EN | 4 | 2 | 2/0 | 1 | 2/0 | No |

**Every single translation call across the entire run succeeded** (HTTP 200) — zero authentication or service failures in this run, confirming the dedicated Translator credentials from Step 5.6a/5.6b remain valid and stable across a larger, more realistic call volume (48 total real calls: 34 partial-time B1 calls + 14 authoritative Final calls; B3's flush calls are counted within the "requests" column, one per non-empty case).

## 6. Latency measurements

**PROVEN (live)**: individual translation request latency (T1→T2) was **100-270ms** for 47 of the 48 real calls in this run. **OBSERVED, one outlier**: the very first call of the entire run (case A, partial #3) measured **13,791ms** — nearly 14 seconds — with every subsequent call in the same run back to the normal 100-270ms range. This is most plausibly a one-time cold-start cost (DNS resolution, TLS handshake, `HttpClient`/connection-pool warm-up for the very first request of the process) rather than a per-utterance or per-connection recurring issue — unlike the EN→DE Speech-SDK connection-latency anomaly documented in Steps 3/4/5 (which recurred on nearly every EN→DE case), this outlier appeared exactly once, regardless of direction, and did not recur even for the very next EN→DE case (B). **UNVALIDATED**: whether this outlier is truly one-time-only would need a second live run to confirm; not re-run in this step to avoid unnecessary API calls per the "minimal" spirit of these experiments. Source-commit-to-translation-request latency (T0→T1) measured consistently as ~0ms in every case, because the experiment issues the translation request synchronously immediately after detecting a stable source commit — this is an artifact of this experiment's own code structure (no queuing/batching delay), not a claim about real-world dispatch latency in a future streaming pipeline. **Per instruction, no claim of physical audible latency is made anywhere in this document** — TTS and playback are not part of this experiment.

## 7. Duplication/revision findings

**PROVEN (unit test + live)**: B1 and B3 never contradicted or retracted already-committed SOURCE content (0 `RevisionCount` in every live case) — this is a direct consequence of Policy A's own already-verified regression-safety (Steps 2/2.5), reused unmodified. **PROVEN (unit test)**: a genuine source contradiction (`PartialRegression_DoesNotProduceASpuriousStableCandidate`) correctly produces zero new translation requests rather than a spurious one. **PROVEN (unit test)**: a failed Translator call maps to `TranslationCommitState.RevisionRequired` and `HasNewCandidate=false`, never silently treated as a usable candidate (`TranslationFailure_MapsToRevisionRequired_NotSilentlyIgnored`). **No case in this live run ever exercised the translation-side contradiction path** (a later translation candidate contradicting an earlier one) — B1 always wholesale-replaces (so contradiction is structurally impossible for it) and B3 never fired more than once mid-utterance in this data (§4), so this specific path remains **UNVALIDATED live**, though it is unit-tested (`StrategyC_ContradictingTranslatedText_WithheldNotAppended`-equivalent coverage exists for the underlying `IncrementalTranslationProviderExperiment`, reused unmodified from Step 5.5).

## 8. Quality observations

Per instruction, the following are **explicitly labeled experimental human/inspection observations, not formal quality guarantees** — a small number of live translations were read directly from the log/console output for this assessment:

- Case A's final translation ("I would like to schedule a meeting tomorrow." → German) and case F's German→English translation both read as **grammatically coherent and semantically faithful** on inspection — consistent with Step 5.6b's smoke-test observations.
- Case I (incomplete sentence, "I was going to say something but") — the Translator API returned a **complete-looking German sentence** for a deliberately incomplete English fragment; this is not a defect in the experiment, but is worth flagging: a real streaming consumer cannot rely on the Translator's output *shape* to signal source incompleteness — the source's own `IsFinal`/commit state must carry that information, exactly the separation this step's state-machine design (§9 below) requires.
- No case in this run produced an obviously nonsensical or grammatically broken translation on inspection, but this is a **very small, non-blinded, single-reviewer sample** (14 cases, read once) — not a substitute for structured human evaluation.

## 9. Distinguishing source commitment from translation commitment (§4 of the governing instructions)

This is implemented, not just conceptual: `SourceCommitState` (Provisional/Stable/Final, produced by Policy A) and `TranslationCommitState` (Candidate/StableCandidate/RevisionRequired/Authoritative, produced by this step's mapping over `IncrementalTranslationProviderExperiment`'s results) are two genuinely separate enums on two separate fields of `StreamingTranslationStepResult` — never collapsed into one. **PROVEN**: a source segment reaching `Stable` never implies a translation state better than `Candidate` for B1 (by construction, every B1 result this run was `Candidate`, reflecting that its wholesale-replace design makes every one of its outputs perpetually subject to full rewrite, regardless of how stable the underlying source is). The one metric worth flagging honestly here: B1's small-but-nonzero duplicate-token counts (§3) are best explained by this separation itself — the Translator API does not translate a growing English/German prefix in a perfectly linear, prefix-preserving way (grammar, word order, and even word choice can shift between translating "I would like" and "I would like to schedule a meeting tomorrow"), so comparing token multisets across two *different* translations of two *different* (prefix-related but non-identical) source texts will show small mismatches that are not really "duplication" in the naive sense — this is a genuine, now-measured limitation of the token-multiset reconciliation method (already flagged as non-semantic in Step 5.5 §7), not evidence that B1 literally repeats words in its own output stream.

## 10. Limitations

- **B3's implementation, as built, provides no measured incremental benefit against realistic ASR-style partial text** (§4) — its sentence-boundary gate essentially never fires before Final in this data shape.
- **Small sample**: 14 cases, one live run, hand-written (not real-audio-driven) partial sequences — real Azure ASR partial timing/content (word-by-word arrival rate, actual mid-utterance revision patterns) was not used; this experiment used text-only, manually-authored progressions, consistent with every prior text-translation-focused step in this series but a real limitation versus true end-to-end ASR-driven testing.
- **Reconciliation remains structural/token-based, not semantic** — naturalness (§8) is a small, non-blinded inspection, explicitly not a formal guarantee.
- **The 13.8-second latency outlier (§6) is unexplained** and not reproduced/investigated further this step.
- **Translation-side contradiction/revision path is UNVALIDATED live** (§7) — only unit-tested.

## 11. Recommendation

**Answering the critical question directly**: *"Can we obtain sufficiently stable translated segments early enough that a future streaming TTS layer could safely speak them without routinely producing duplicated, contradictory, or obviously incomplete speech?"* — **Not yet, with either strategy as currently implemented, based on measured results, not assumption.** B1 never produces a `StableCandidate`-class result (every one of its outputs is `Candidate`, i.e. always subject to full rewrite) — a streaming TTS layer built on B1 alone would need to re-speak/replace its own output on nearly every commit, which is exactly the "routinely producing duplicated ... speech" failure mode this step was asked to rule in or out; the reconciliation data suggests it would rule it *in* as a real risk. B3, which is architecturally the one designed to produce genuinely-incremental `StableCandidate` results, **never actually produced one in this realistic data** — so it cannot currently be recommended either, not because it failed a test, but because it was **never meaningfully exercised** by the kind of partial text real ASR produces. **The concrete, evidence-based next step, if pursued** (not authorized by this step): redesign B3's boundary trigger to not depend on literal punctuation inside the committed segment — e.g. a word-count or time-based rolling window (already explored conceptually in `commit-policy-shadow-evaluation.md` for the SOURCE side; an analogous idea has not yet been tried for the TRANSLATION side) — and re-run this same realistic-partial-sequence methodology against it before any streaming TTS work is considered. This step's honest conclusion is a **not-yet**, grounded in what was actually measured, not a qualitative guess.
