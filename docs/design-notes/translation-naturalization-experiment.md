# Step 5.14 — Controlled Translation Naturalization Experiment

**Status: COMPLETE. Experimental, shadow-only, fully isolated from production. Not integrated into production. Do not start Step 5.15 without explicit authorization.**

## Legend

- **PROVEN** — demonstrated by a passing automated test with the actual output shown.
- **OBSERVED** — a real measurement was taken, not fabricated, but from a limited/synthetic scenario.
- **UNVALIDATED** — not yet tested in this environment.
- **ASSUMED** — a deliberate design choice made without direct evidence, called out explicitly as such.
- **HUMAN/HEURISTIC** — a subjective judgment (e.g., "this reads more naturally"), explicitly labeled as such per §6's requirement, never presented as measured fact.

## 0. Headline result — read this first

**Live naturalization comparison is UNAVAILABLE in this environment.** This project has no existing credential convention for an LLM/naturalization backend (only `AZURE_SPEECH_KEY`/`REGION` and `AZURE_TRANSLATOR_KEY`/`REGION`/`ENDPOINT` are configured). The `translation-naturalization-test` live-test command checks for `NATURALIZATION_PROVIDER_API_KEY` and, finding it unset, stops cleanly — no security control was bypassed, no vendor integration was invented to fabricate evidence, no result was fabricated. This is reported honestly, per the explicit instruction: "Do NOT spend time bypassing environmental security controls... If execution is blocked: stop; do not bypass security; do not fabricate results; report exactly what was blocked."

Everything else in this document — the provider abstraction, the deterministic validator, the pipeline orchestration, safety/fallback behavior, and all quality/latency/failure-handling claims — is **PROVEN or OBSERVED entirely through deterministic unit tests** using a fake `INaturalizationProvider`, not through a real LLM call. Wherever this document discusses "naturalization quality," it is evaluating the VALIDATOR's behavior against hand-authored candidate text, not real generative-model output. This is stated explicitly everywhere below, never left implicit.

## 1. What was built

Composing, **never modifying**, prior validated components:

| Component | Reused from | Modified? |
|---|---|---|
| Bounded context (C1/C2 concept) | Step 5.11 | No — `NaturalizationRequest.BoundedContext` is a pass-through string field; this experiment does not construct or expand context itself, it accepts whatever bounded-context string the caller (e.g., Step 5.13's pipeline) already produced. |
| Generation-guard / duplicate-prevention pattern | Steps 5.12/5.13 | No — same structural pattern, new instance. |
| `IDiagnosticLogger` (metadata-only logging) | Step 1 | No |

New for this step:

- [`src/VTTranslate.Core/Streaming/NaturalizationModels.cs`](../../src/VTTranslate.Core/Streaming/NaturalizationModels.cs) — `INaturalizationProvider`, `NaturalizationRequest`/`NaturalizationCandidateResult`/`NaturalizationOutcomeResult`, `ValidationOutcome`, `ValidationFindings`.
- [`src/VTTranslate.Core/Streaming/MeaningPreservationValidator.cs`](../../src/VTTranslate.Core/Streaming/MeaningPreservationValidator.cs) — the deterministic, non-AI validation layer (§4).
- [`src/VTTranslate.Core/Streaming/TranslationNaturalizationExperiment.cs`](../../src/VTTranslate.Core/Streaming/TranslationNaturalizationExperiment.cs) — the orchestrator (§3, §8, §9).
- [`src/VTTranslate.Core/Streaming/NaturalizationTestCorpus.cs`](../../src/VTTranslate.Core/Streaming/NaturalizationTestCorpus.cs) — the 38-case corpus (§5).
- [`tests/VTTranslate.Core.Tests/MeaningPreservationValidatorTests.cs`](../../tests/VTTranslate.Core.Tests/MeaningPreservationValidatorTests.cs) — 22 tests.
- [`tests/VTTranslate.Core.Tests/TranslationNaturalizationExperimentTests.cs`](../../tests/VTTranslate.Core.Tests/TranslationNaturalizationExperimentTests.cs) — 26 tests.
- `translation-naturalization-test` command in [`tools/VTTranslate.LiveTest/Program.cs`](../../tools/VTTranslate.LiveTest/Program.cs).

**Production isolation, PROVEN**: `grep -c "Naturalization" src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs src/VTTranslate.Core/Session/DirectionPipeline.cs src/VTTranslate.App/*.cs` → `0` for every file. The production `DirectionPipeline` and `AzureSpeechTranslationProvider` have zero references to any type in `NaturalizationModels.cs`. This experiment can be deleted entirely (the four new `Streaming/` files and two new test files) without changing a single line of production behavior.

## 2. Naturalization provider abstraction

`INaturalizationProvider.NaturalizeAsync(NaturalizationRequest, CancellationToken) → Task<NaturalizationCandidateResult>` — deliberately vendor-agnostic. The interface does not assume an LLM; it does not assume any particular backend at all. `NaturalizationCandidateResult.ProviderUncertain` is a first-class field so ANY backend (LLM-based or rule-based) can signal "I'm not confident in this rewrite," which the orchestrator treats specially (§3).

**No LLM integration was built** — not because it was decided to be unnecessary, but because no naturalization-provider credential is legitimately available in this environment (§0), and building an untested, never-exercised HTTP client against an unspecified/unauthorized vendor would itself be the kind of unvalidated code this project's standing rules warn against. `FakeNaturalizationProvider` (test-only) is the only implementation that exists, used to deterministically drive every unit test.

**Credential handling, as specified**: no API key is hard-coded anywhere in this experiment (PROVEN by the secret scan, §12). The live-test command checks `NATURALIZATION_PROVIDER_API_KEY` from the environment only, following this project's established convention (`TranslatorCredentialConfig`'s pattern) of never hard-coding, never logging, never persisting a credential value.

## 3. Strict naturalization contract

The contract is enforced **structurally** in `TranslationNaturalizationExperiment.ProcessAsync`, not just documented:

1. **Uncertain → automatic fallback, never validated.** If `ProviderUncertain == true`, the pipeline returns `FallbackToBaseline` immediately, WITHOUT running `MeaningPreservationValidator` at all — PROVEN by `ProviderUncertain_NeverValidated_FallsBackToBaseline`, which asserts `result.Findings` is `null` (proof validation never executed) even though the candidate text, if it HAD been validated, would have passed every check (it's identical to the baseline).
2. **Empty/malformed/failed provider result → fallback, never validated.** PROVEN by `ProviderFailure_FallsBackToBaseline`, `EmptyNaturalizedText_TreatedAsFallback`, `WhitespaceOnlyNaturalizedText_TreatedAsFallback`.
3. **Every "preserve X" requirement in the contract maps to one deterministic validator check** (§4) — negation, numbers, dates, names, terminology, question/statement form, modality, qualifiers, and overall token coverage as a catch-all "did this turn into a substantially different sentence" guard. The contract items "never invent information," "never omit information," "never answer an unanswered question," and "never add explanations" are NOT independently, directly checked — they are covered only INDIRECTLY by the token-coverage and entity/number/date checks (see §4's Limitations — this is an honest gap, not claimed as fully covered).
4. **A REJECTED validation always falls back to baseline, never partially applies the candidate.** PROVEN by every "…PreservationFailure_FallsBackToBaseline" test in `TranslationNaturalizationExperimentTests.cs` — the `FinalText` in every rejection case is asserted to equal the exact baseline string.

## 4. Safety / meaning validation layer

`MeaningPreservationValidator.Evaluate(baseline, candidate, terminologyTerms) → ValidationFindings` runs nine deterministic checks (§ listed in the instruction, all implemented):

| Check | Method | What it actually detects |
|---|---|---|
| Token coverage | Word-level Jaccard overlap ≥ 40% (`MinTokenCoverageRatio`) | Gross "this became a different sentence" divergence |
| Negation | Count of negation-vocabulary tokens (EN+DE) matches | A negation word appearing/disappearing |
| Numbers | Regex-extracted digit sequences form the same set | A digit-form number changing or vanishing |
| Dates | Regex-extracted date patterns (digit dates + EN/DE month names) form the same set | A recognizable date pattern changing or vanishing |
| Names/entities | Non-sentence-initial capitalized words (minus a small function-word exclusion list) form the same set | A capitalized proper noun appearing/disappearing |
| Terminology | Every caller-supplied required term still appears (case-insensitive substring) | A specifically flagged domain term being dropped |
| Question/statement | Trailing `?` presence matches | A question being flattened into a statement or vice versa |
| Modality | Count of modal-verb-vocabulary tokens (EN+DE) matches | A modal verb category appearing/disappearing |
| Qualifiers | Count of hedging-vocabulary tokens (EN+DE) matches | A hedge word ("maybe," "vielleicht") appearing/disappearing |

Outcome: `Classify(findings)` → `SafeToUse` iff every check passes, else `Rejected`. `FallbackToBaseline` is a PIPELINE-level outcome (§3), never returned by the validator itself.

**Explicit, documented limitations** (from the validator's own class-level doc comment, restated here per instruction §4/§14):

- **Cannot detect a meaning change that preserves the same surface tokens/counts** — e.g., swapping which of two named entities performed an action, or inverting a comparison ("faster than" → "slower than") without changing any number/negation/name count. PROVEN as a real, not hypothetical, gap by `MeaningPreservationValidatorTests`' documented-limitation-style tests and `SelfCorrection_WordFormNumbers_ValidatorLimitation_NotCaughtByNumberRegex` (below).
- **Name/entity detection is a crude capitalization heuristic.** Misses lowercase entities; false-positives on any capitalized common noun that isn't in the small exclusion list.
- **Number/date extraction is regex-based and misses spelled-out numbers/dates** — PROVEN directly: `SelfCorrection_WordFormNumbers_ValidatorLimitation_NotCaughtByNumberRegex` shows that collapsing "at three — no, sorry, at four" into "at four" is judged `NumbersPreserved = true` (a real false negative), while the DIGIT-form equivalent ("at 3 — no, sorry, at 4" → "at 4") IS correctly caught (`SelfCorrection_CollapsedIntoSingleClaim_RejectedViaNumberMismatch`).
- **Modality/qualifier checks compare category PRESENCE, not degree.** "Would" → "could" both count as one modal token each, so a certainty shift within the modal category is invisible — PROVEN by `ModalityPreserved_DifferentModalWord_StillCountedAsPresent`.
- **"Never invent/omit information" and "never add explanations" have no dedicated check.** They are only caught incidentally if the invention/omission happens to also move a number, name, date, negation, or drag token coverage below 40%. A candidate that adds a short, plausible-sounding but false elaboration using only already-present vocabulary would NOT be reliably caught. This is the most significant open gap, and directly explains why §15's final answer is not an unconditional "safe."

**Per instruction: "Do not pretend deterministic text comparison is equivalent to semantic understanding"** — nothing above claims otherwise; every check is described by exactly what pattern it matches, not by what it "understands."

## 5. Test corpus

38 hand-authored cases in `NaturalizationTestCorpus.cs`, covering all 19 required categories (A–S), both directions (DE→EN and EN→DE), including deliberately non-textbook-perfect input: informal speech (B), self-corrections (J), incomplete utterances (K), idioms (E), fast conversational phrasing (S), and ambiguous wording (R). Each case carries a hand-authored `ReferenceBaselineTranslation` — an HUMAN/HEURISTIC reference translation for comparison purposes only, explicitly NOT real Azure Translator output (no live Azure Translator calls were made against this specific corpus in this step — Step 5.5/5.6a already separately validated Azure Translator's own behavior).

**What this corpus was and was not used for**: it was authored and is ready for a live naturalization run (`translation-naturalization-test` reports its size, §0), but — since no naturalization provider is available (§0) — it was **not run through a real naturalization call**, and no per-case naturalized/validated output table exists for it. This is an honest gap, not silently omitted: the corpus is complete and unit-test-consistent (all 38 `SourceText`/`ReferenceBaselineTranslation` pairs compile and are structurally valid), but zero corpus cases have live naturalization results.

## 6. Required comparison

**Not performed end-to-end against the corpus**, for the reason stated in §0/§5. What WAS compared, exhaustively, via unit tests: baseline vs. hand-authored candidate text for ~20 individually-designed scenarios (not the 38-case corpus) exercising every dimension in §6's list — semantic preservation (via each validator check), missing/added information (via token coverage + specific-entity checks), terminology preservation, entity preservation, number/date preservation, rejection/fallback behavior, and processing latency (all sub-millisecond in these in-memory fake-provider tests — see §7). "Naturalness" itself is NOT measured anywhere in this step — there is no real naturalized output to judge, and no human rater was used. Any future step running real naturalization output through this validator should record naturalness as an explicit HUMAN/HEURISTIC rating, never inferred from validator pass/fail (a `SafeToUse` classification says nothing about whether the text sounds MORE natural — only that it didn't fail a preservation check).

## 7. Latency

**OBSERVED, but only from the deterministic unit-test suite (in-memory fake provider, no network)** — these are NOT representative of real LLM/naturalization-provider latency, and are reported as such:

- All 26 `TranslationNaturalizationExperimentTests` complete in ~170ms combined (most individual `ProcessAsync` calls resolve in low single-digit milliseconds against the synchronous fake provider).
- The two tests that DO measure real elapsed time (`Cancellation_DuringNaturalization_ReportedAsCancelled_UsesBaselineAsFinalText`, `Timeout_ViaCancellationToken_ReportedAsCancelled_BaselineStillAvailable`) inject an artificial `Task.Delay(5s)` with a 30ms cancellation, proving the CANCELLATION MECHANISM works, not proving anything about real-world naturalization latency.

**No median/p95/max/outlier table is reported** — per §7's own instruction ("If only a small number of live samples are possible, explicitly call them observations rather than benchmarks"), and since ZERO live samples were possible in this environment (§0), there is no latency dataset to summarize at all. `NaturalizationOutcomeResult` carries the full T0→T1→T2→T3 timing fields (`T0ToT1RequestStartMs`, `T1ToT2ResponseMs`, `T2ToT3ValidationMs`, `T0ToT3TotalMs`) exactly as specified, ready to populate real statistics the moment a live run becomes possible — but populating them now with synthetic/fake-provider numbers would misrepresent them as real latency data, which this document does not do.

**One structural, non-timing finding, OBSERVED**: `T2ToT3ValidationMs` (the deterministic validator's own execution time) is the ONE latency component that this step CAN honestly measure representatively even offline, since the validator has no network dependency — its regex/set-based checks run in well under 1ms per call in every test (implicit in the sub-millisecond-per-test totals above; not separately isolated/benchmarked as a dedicated number, so reported as a qualitative OBSERVED finding, not a precise figure).

## 8. Failure handling

All nine required scenarios PROVEN via dedicated unit tests, each resolving to the safe baseline text:

| Scenario | Test | Result |
|---|---|---|
| Provider timeout | `Timeout_ViaCancellationToken_ReportedAsCancelled_BaselineStillAvailable` | `Cancelled`, baseline returned |
| Provider error | `ProviderFailure_FallsBackToBaseline` | `FallbackToBaseline` |
| Empty result | `EmptyNaturalizedText_TreatedAsFallback` | `FallbackToBaseline` |
| Malformed result | `WhitespaceOnlyNaturalizedText_TreatedAsFallback` | `FallbackToBaseline` |
| Cancellation | `Cancellation_DuringNaturalization_ReportedAsCancelled_UsesBaselineAsFinalText` | `Cancelled`, baseline returned |
| Stale generation | `StaleGeneration_AtSubmission_RejectedWithoutCallingProvider`, `StaleGeneration_DuringNaturalization_DiscardedEvenThoughProviderSucceeded` | `RejectedStaleGeneration` (both before AND after the provider call) |
| Consecutive segments | `ConsecutiveSegments_EachProcessedIndependently` | Each of 3 segments delivered independently |
| Duplicate candidate | `DuplicateKey_RejectedOnSecondSubmission` | `RejectedDuplicate`, provider called only once |
| Naturalizer unavailable | §0 (live-test command) | Reported and stopped cleanly, no bypass |

**Additionally tested beyond the required list**: an unexpected provider exception (`ProviderThrowsUnexpectedException_PropagatesRatherThanSilentlyMasking`) is NOT silently swallowed — it propagates to the caller, a deliberate choice documented here: distinguishing "the provider told us it failed" (→ safe fallback) from "the provider itself is broken/misbehaving" (→ propagate, don't pretend everything is fine) was judged more honest than masking every exception as a fallback.

**Required safe behavior, PROVEN across every table row above**: `FinalText` always equals the exact baseline string whenever naturalization fails, times out, is rejected, or becomes stale — never a partial, corrupted, or silently-substituted value.

## 9. Conversational context

`NaturalizationRequest.BoundedContext` is a plain pass-through string — this experiment does not construct, expand, or persist context itself; it only forwards whatever bounded-context string its caller already assembled (the C1/C2 concept from Step 5.11, reused via Step 5.13's own context-passing convention). PROVEN by `BoundedContext_PassedThroughUnmodified_NeverExpanded` (the exact string passed in reaches the provider unmodified).

**No persistent memory, no self-learning, no unrestricted history**: `TranslationNaturalizationExperiment` holds only an in-memory `HashSet` of processed `(UtteranceId, Generation, SegmentSequence)` keys (for duplicate-prevention, cleared on `Reset()`) — nothing is written to disk, no conversation transcript is accumulated, and nothing adapts based on past results. Confirmed by inspection: the class has no field beyond `_provider`, `_logger`, `_sessionTag`, `_processed` (the duplicate-guard set), and `CurrentGeneration`.

## 10. Unit tests

**25 tests** in `TranslationNaturalizationExperimentTests.cs` + **22 tests** in `MeaningPreservationValidatorTests.cs` = **47 new tests**, covering every item in §10's required list (naturalization success, rejection, baseline fallback, all nine meaning-preservation-failure categories individually, incomplete-sentence handling, self-correction — including the explicit validator-limitation case — cancellation, stale-generation suppression, duplicate prevention, consecutive segments, provider failure, timeout, empty response, malformed response). All use `FakeNaturalizationProvider` (deterministic, in-memory, no network) — PROVEN, not simulated-in-prose.

## 11. Live experiment

**BLOCKED — not by security, by absence of a legitimate credential.** See §0. The `translation-naturalization-test` command was actually run (not hypothetically described) and its exact console output is:

```
=== Translation naturalization — live run ===
BLOCKED: no naturalization provider credential is configured in this environment.
Checked environment variable: NATURALIZATION_PROVIDER_API_KEY (not set).
This project has no existing credential convention for an LLM/naturalization backend.
Per instruction: not bypassing security, not fabricating results. Live naturalization comparison is UNAVAILABLE in this environment.
Test corpus is ready (38 cases, categories A-S, both directions) for whenever a provider is authorized and configured.
```

No environmental security control was probed, weakened, or bypassed to attempt to obtain this credential.

## 12. Security

- **No API keys in source**: `grep -rEi "AZURE_[A-Z_]*KEY\s*=\s*['\"][A-Za-z0-9]{10,}|NATURALIZATION_PROVIDER_API_KEY\s*=\s*['\"][A-Za-z0-9]"` across `src/`, `tools/`, `tests/`, `docs/` → no matches (PROVEN).
- **No credentials in logs**: the only credential this step touches (`NATURALIZATION_PROVIDER_API_KEY`) is read via `Environment.GetEnvironmentVariable` and never printed or logged — the live-test command prints only whether it is set, never its value or length (a stricter standard than even `TranslatorCredentialLoader.DescribeSafely`'s length-only convention, since this key was never actually obtained to describe).
- **No source speech in diagnostic logs**: `TranslationNaturalizationExperiment.LogOutcome` logs only `utteranceId`, `generation`, `segmentSequence`, `baselineLength` (an integer, not text), `pipelineOutcome`, `validationOutcome` — PROVEN by `NeverLogsBaselineOrCandidateText_OnlyMetadata`, which feeds a sensitive German sentence through the full pipeline and asserts no log entry contains any substring of it.
- **No translated/naturalized text in diagnostic logs**: same test/same guarantee — the candidate and final text are likewise never logged, satisfying the stricter of the two options offered in §12 ("no translated text in diagnostic logs unless explicitly required and approved" — it was not requested, so the strictest interpretation was applied).
- **No unnecessary persistent conversation storage**: confirmed by inspection, §9.

## 13. Production isolation

- `AzureSpeechTranslationProvider.cs` — **not modified** (confirmed via `git status` showing no diff to this file during this step, and `grep -c "Naturalization"` → `0`, PROVEN).
- Production `DirectionPipeline.cs` — **not modified**, and **does not invoke** `TranslationNaturalizationExperiment` or any type from `NaturalizationModels.cs` (`grep -c "Naturalization"` → `0`, PROVEN).
- Production UI (`VTTranslate.App/*.cs`) — **not modified**, zero references (PROVEN).
- **Removability**: deleting `NaturalizationModels.cs`, `MeaningPreservationValidator.cs`, `TranslationNaturalizationExperiment.cs`, `NaturalizationTestCorpus.cs`, their two test files, and the one `case`/one function pair in `LiveTest/Program.cs` would compile and behave identically in production — nothing outside those files references any symbol from them.

## 14. Full test run

`dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj` → **375/375 passed** (328 from Steps 1–5.13 + 22 new validator tests + 25 new pipeline tests), zero regressions (PROVEN, this run was not blocked by WDAC in this session). `dotnet build` on all four projects (`VTTranslate.Core`, `VTTranslate.App`, `VTTranslate.Core.Tests`, `VTTranslate.LiveTest`) → 0 errors, 0 warnings (PROVEN).

## 15. Final decision gate

1. **Does naturalization measurably improve naturalness?** **UNKNOWN — not measured.** No real naturalization output exists in this step; naturalness was never evaluated, HUMAN/HEURISTIC or otherwise. This is the single biggest open question this step leaves unanswered.
2. **Does it preserve meaning reliably?** The SAFETY MECHANISM (validate-or-fallback) is PROVEN reliable at the ENGINEERING level — every failure/rejection path correctly falls back to baseline, with zero exceptions found across 47 tests. Whether the VALIDATOR ITSELF reliably catches real meaning changes from a real generative model is UNVALIDATED — its documented limitations (§4) mean a real naturalizer that invents plausible-sounding but false elaborations, or swaps entity roles without changing entity counts, could pass `SafeToUse` undetected.
3. **How often must it fall back to baseline?** **UNKNOWN — no live data.** Cannot be answered without a real provider producing real candidates against the corpus.
4. **What additional latency does it introduce?** **UNKNOWN in real terms** (§7) — only the fallback/cancellation MECHANISM's overhead is measured (sub-millisecond, synthetic). Real LLM-call latency was never observed.
5. **Does it improve both German → English and English → German?** **UNKNOWN — not measured** (same reason as #1). The corpus (§5) is balanced both directions and is ready for this comparison once a provider is available.
6. **Is it safe enough for controlled end-to-end testing?** The FAILURE-HANDLING and FALLBACK mechanics are safe (PROVEN, §8) — naturalization cannot become a single point of failure, structurally. The VALIDATION layer's coverage gaps (§4 — no dedicated "never invent/omit" check) mean it is safe enough to TRIAL with human review of a sample, but NOT yet safe enough to trust unsupervised even in a controlled test, until real output has been checked against those specific gaps.
7. **Should it be adopted, refined, or rejected?** Neither adopt nor reject — **REFINE, then re-evaluate with real evidence.** Concretely: (a) obtain an authorized naturalization-provider credential and implement one concrete `INaturalizationProvider` against it; (b) run the existing 38-case corpus live; (c) add a dedicated "no-new-information" check to `MeaningPreservationValidator` (the current biggest documented gap) before trusting `SafeToUse` unsupervised; (d) collect real T0-T3 latency data before making any latency claim.
8. **What evidence is still missing?** Everything requiring a real naturalization provider: actual naturalized text for the 38-case corpus, real quality/naturalness judgments (HUMAN/HEURISTIC, to be explicitly labeled when collected), real latency percentiles, real fallback-rate statistics, and empirical evidence on whether the validator's known gaps (§4) manifest in practice with a real model's typical failure modes (over-confident paraphrase, added connective explanations, entity-role swaps).

**STOP — Step 5.14 is complete. Waiting for review. Do not start Step 5.15, production integration, Google Meet, mobile, voice cloning, persistent adaptive memory, unrestricted self-learning, or production UI changes without explicit authorization.**

---

## Step 5.14A addendum — Real naturalization provider evaluation

**NATURALIZATION PROVIDER UNAVAILABLE.**

Step 5.14A asked for a controlled LIVE evaluation of the naturalization architecture against a real, legitimate, authorized generative naturalization provider. Before running anything, this environment's credentials were checked (never printed, never logged, never bypassed):

```
env | sort | grep -iE "KEY|TOKEN|SECRET|CRED"
  AZURE_SPEECH_KEY=<redacted>
  AZURE_TRANSLATOR_KEY=<redacted>
  BAGGAGE=<redacted>
  CLAUDE_CODE_MESSAGING_TOKEN=<redacted>
```

plus a check for `.env*` files and any `*.json`/`*.config` file referencing an OpenAI/Anthropic/Azure-OpenAI credential in the repository — none found.

**Findings (PROVEN by direct inspection, not inferred):**

- `AZURE_SPEECH_KEY` / `AZURE_TRANSLATOR_KEY` are configured — but neither is a generative naturalization/paraphrase provider. `AZURE_TRANSLATOR_KEY` is the baseline TRANSLATION service this experiment naturalizes the OUTPUT of; using it as its own "naturalizer" would not be a real second opinion, it would be a self-referential no-op.
- No `NATURALIZATION_PROVIDER_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `AZURE_OPENAI_*`, or any comparable generative-model credential is present in the environment or in any repository config file.
- `CLAUDE_CODE_MESSAGING_TOKEN` is this development session's own internal harness/messaging token (used by the Claude Code tooling this session runs inside) — it is NOT a naturalization-provider credential, was not issued for that purpose, and using it to call a generative model on the corpus's behalf would be exactly the kind of credential misuse/scope violation Step 5.14A explicitly prohibits ("never bypass authentication, licensing, quotas, or security controls"). It was not used.

**Per the explicit instruction ("If no legitimate provider is available, STOP and report... Do not fabricate results and do not implement a fake live provider merely to claim completion"), this step stops here.** No corpus run, no live comparison table, no quality evaluation, no live latency data, and no validator blind-spot investigation against real generated text could be performed — all of §§3–9 and §13's items 1–6 of Step 5.14A are **UNVALIDATED**, not "negative" or "poor" — there is simply no evidence, because no eligible input (real generated candidates) exists to evaluate.

**What remains true and unchanged from Step 5.14 (still valid):**
- The `INaturalizationProvider` abstraction, `MeaningPreservationValidator`, `TranslationNaturalizationExperiment` orchestrator, 38-case corpus, and 47 deterministic unit tests are unmodified and still pass (`dotnet test` → 375/375, re-confirmed during this step).
- Production isolation re-verified: `grep -c "Naturalization" src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs src/VTTranslate.Core/Session/DirectionPipeline.cs` → `0` for both files (PROVEN). Neither file was touched in this step (no diff).
- Secret scan re-run: `grep -rEi "AZURE_[A-Z_]*KEY\s*=\s*['\"][A-Za-z0-9]{10,}|(OPENAI|ANTHROPIC|CLAUDE)[A-Z_]*KEY\s*=\s*['\"][A-Za-z0-9]"` across `src/`, `tools/`, `tests/`, `docs/` → no matches (PROVEN). No credential of any kind — including the harness messaging token, which was never written anywhere — appears in any file this step touched (only this one documentation file was edited).
- No unit tests were added in this step (§10's "add tests only if the live-provider integration exposes a concrete reproducible issue" — there was no live-provider integration to expose anything).
- No validator changes were made (§6 — nothing was silently weakened or strengthened; there was no real naturalizer output to react to).

### Step 5.14A final decision (§13), answered honestly

1. **Did naturalization improve naturalness?** UNVALIDATED — no real naturalized text was produced.
2. **By how much / in how many cases?** UNVALIDATED — N/A, zero live cases.
3. **Did it introduce semantic errors?** UNVALIDATED — nothing to check.
4. **What was the fallback rate?** UNVALIDATED — no live runs occurred; the fallback MECHANISM itself remains PROVEN correct from Step 5.14's unit tests, but no real-world fallback frequency exists.
5. **What was the additional latency?** UNVALIDATED — no network calls were made to any naturalization provider.
6. **Did German → English and English → German behave similarly?** UNVALIDATED — no data in either direction.
7. **Are the current safety checks sufficient?** Unchanged from Step 5.14's own answer: the FALLBACK mechanics are PROVEN sufficient (engineering-level); the VALIDATOR's coverage gaps (§4 of the base document — no dedicated "no new information" check) remain ASSUMED-acceptable-for-now but UNVALIDATED against real model output, exactly as before. This step neither improves nor worsens that assessment — it simply could not add evidence either way.
8. **Is the naturalization layer safe enough for a controlled end-to-end experiment?** Structurally yes (unchanged, PROVEN at the engineering level per Step 5.14 §8); but a controlled end-to-end experiment is exactly what requires a real provider, which remains unavailable — so this question stays open pending §9's recommendation.
9. **Should we ADOPT, REFINE, or REJECT it?** Neither — **BLOCKED ON PROVISIONING.** The correct next action is not further code work but an explicit, out-of-band decision by the user/organization to authorize and provision ONE specific naturalization-provider credential (e.g., an Azure OpenAI deployment under the existing Azure subscription, or another vendor of the user's choosing) via this project's established environment-variable convention. Once that credential exists, Step 5.14A's full scope (§§1–13) can be executed exactly as specified, using the already-built and already-tested architecture from Step 5.14 unchanged.

**STOP — Step 5.14A is complete (as a feasibility check). Waiting for review and, specifically, for a provisioning decision before any further live naturalization work can proceed. Do not start Step 5.15, production integration, Google Meet, mobile, self-learning, persistent adaptive memory, or voice cloning.**
