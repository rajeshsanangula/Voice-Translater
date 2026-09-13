# Step 5.14A — Gemini Naturalization Provider Experiment

**Status: COMPLETE. Experimental, shadow-only, fully isolated from production. Not integrated into production. Do not start Step 5.15 without explicit authorization.**

## Legend

- **PROVEN** — demonstrated by a passing automated test with the actual output shown.
- **OBSERVED** — a real measurement was taken (real Gemini API calls, real Azure Translator calls), not fabricated, but from a small/rate-limited live sample.
- **UNVALIDATED** — not yet tested.
- **ASSUMED** — a deliberate design choice made without direct evidence.
- **AUTOMATED VALIDATION** — a result of `MeaningPreservationValidator`'s deterministic checks.
- **HUMAN/QUALITATIVE ASSESSMENT** — a subjective judgment made by reading the actual text, explicitly labeled as such, never presented as a measured/automated fact.

---

## 1. Objective

Determine whether Google Gemini, used as a real generative naturalization provider behind the existing Step 5.14 `INaturalizationProvider` abstraction, can measurably improve the conversational naturalness of Azure Translator's baseline German↔English output while reliably preserving meaning — using the already-built, unmodified Step 5.14 architecture (provider abstraction → `MeaningPreservationValidator` → accept/reject/fallback) and the existing 38-case corpus.

## 2. Provider / model used

**Google Gemini**, via the official `v1beta` `generateContent` REST endpoint. Per the explicit instruction not to assume a model name, the model was discovered programmatically:

1. `GET /v1beta/models?key=...` was called to list every model the configured API key can see.
2. Candidates unsuitable for text naturalization (TTS, image, video, audio, embedding, live, robotics, research-tooling, transcription models) were filtered out.
3. Remaining candidates were **probed with a real, minimal `generateContent` call**, in preference order (stable, non-lite Flash models first, then `-latest` aliases, then everything else) — because, discovered empirically during this exact step, `ListModels` can list a model ID that is **no longer actually callable** for the current key/tier. `models/gemini-2.5-flash` is a concrete, PROVEN example: it appears in the list, but a direct call returns `HTTP 404` — `"This model models/gemini-2.5-flash is no longer available to new users."` Trusting the list without probing would have silently picked a broken model.
4. **Model actually used: `gemini-flash-latest`** — the first candidate that returned a real HTTP 200 from `generateContent`. This is recorded here as the model identifier only; no credential was ever recorded, printed, or logged (verified, §13).

## 3. Configuration

- Credential: read **only** from `GEMINI_API_KEY` (environment variable), via `GeminiNaturalizationProvider.TryLoadApiKey()`. Never hard-coded, never printed, never logged (§13).
- Baseline translation: real Azure Translator Text v3.0 calls via the existing, unmodified `AzureTranslatorTextProvider` (Step 5.5/5.6a), using the existing dedicated `AZURE_TRANSLATOR_KEY`/`REGION` credentials.
- Bounded context: `null` for every corpus case — each of the 38 cases is an independent single utterance, not part of a continuous conversation, so there is no preceding committed segment to pass as C1/C2 context. This is consistent with, not a deviation from, the Step 5.11 bounded-context concept (ASSUMED: single-utterance cases correctly have empty context).
- Validator: the existing, **completely unmodified** `MeaningPreservationValidator` from Step 5.14.
- Orchestrator: the existing, **completely unmodified** `TranslationNaturalizationExperiment` from Step 5.14.
- New code added: `GeminiNaturalizationProvider.cs` (the concrete provider) and one live-test command (`translation-naturalization-gemini-test`) in `VTTranslate.LiveTest`. No existing Step 5.14 file was modified.

## 4. Corpus

The full, unmodified 38-case `NaturalizationTestCorpus` (Step 5.14) — categories A–S, both German→English and English→German — was run in its entirety, in order. No case was skipped, replaced, or hand-picked.

## 5. Experimental methodology

For each of the 38 cases, in sequence:

1. **T0**: call the real Azure Translator baseline translation for the case's `SourceText`.
2. **T1**: submit the baseline text to `TranslationNaturalizationExperiment.ProcessAsync` (unmodified Step 5.14 orchestrator), which calls `GeminiNaturalizationProvider.NaturalizeAsync`.
3. **T2**: Gemini's response is received.
4. **T3**: `MeaningPreservationValidator.Evaluate` completes (only if Gemini succeeded and was not flagged uncertain).
5. Final selected output = the naturalized candidate if `SafeToUse`, else the baseline (per the frozen Step 5.14 fallback contract).

**Rate-limit handling (methodological, not a validator or provider-quality change)**: the free-tier API key hit `HTTP 429` (rate limit) partway through the run. Per instruction ("do not fabricate results," "do not weaken the validator"), a case that returns `HTTP 429` is retried with backoff (15s/30s/45s, up to 4 attempts total) — a 429 is a quota condition, not a genuine naturalization outcome, so counting it as-is on the first attempt would misrepresent the provider's real capability. Two full runs were performed: the first (no retry logic) surfaced the rate-limit behavior; the second (with retry/backoff, reported below as the primary result) confirmed the rate limit was **sustained** (every retry across ~90 seconds of backoff still returned 429 for most remaining cases), indicating a **daily/quota-level** limit, not a brief per-minute spike.

## 6. Results — headline

| Metric | Value |
|---|---|
| Total corpus cases run | 38 / 38 |
| Cases with a genuine Gemini API response (not blocked by rate-limit/transient error) | **8** |
| Accepted (`SafeToUse`) | 6 |
| Rejected (validator) | 2 |
| Fallback — genuine provider issue among the 8 real responses | 0 |
| Fallback — rate-limited/transient-error (HTTP 429 or 503), never reached Gemini's actual judgment | 30 |
| Baseline Azure translation failures | 0 |
| Cancelled/timeout | 0 |

**This is the single most important honest caveat in this report: 30 of 38 cases (79%) never received a real Gemini naturalization attempt that completed — they exhausted their retry budget against a persistent rate limit and fell back to baseline by construction, not because Gemini was tested and failed.** The fallback rate reported here is **not** representative of Gemini's real naturalization failure rate; it is dominated by free-tier quota exhaustion. Only the 8 cases that got a real response constitute genuine evidence about Gemini's behavior.

## 7. Validator results (AUTOMATED VALIDATION) — the 8 real cases

| Case | Category | Direction | Baseline | Gemini candidate | Outcome |
|---|---|---|---|---|---|
| A1 | Normal conversation | de→en | "Hello, how are you today?" | "Hello, how are you doing today?" | **SafeToUse** |
| A2 | Normal conversation | en→de | "Mir geht es gut, danke der Nachfrage." | "Mir geht's gut, danke fürs Nachfragen." | **Rejected** — token overlap below 40% |
| B1 | Informal conversation | de→en | "Well, are you all right?" | "Well, are you okay?" | **SafeToUse** |
| B2 | Informal conversation | en→de | "Ja, keine Sorge, alles gut." | (identical — no change) | **SafeToUse** |
| C1 | Business conversation | de→en | "We have to sign the contract by Friday." | (identical — no change) | **SafeToUse** |
| C2 | Business conversation | en→de | "Bitte leiten Sie die Rechnung an die Buchhaltung weiter." | "Leiten Sie die Rechnung bitte an die Buchhaltung weiter." | **SafeToUse** |
| D1 | Technical conversation | de→en | "The server throws a null pointer exception in the production environment." | "The server is throwing a null pointer exception in the production environment." | **Rejected** — required terminology term missing |
| E1 | Idioms | de→en | "That's not my beer." | "That's not my problem." | **SafeToUse** |

Full text for all 38 cases (including the 30 fallback cases, where baseline = final text) is preserved in `test-results/logs/translation-naturalization-gemini/gemini-naturalization-results.tsv` — this corpus is hand-authored experimental data, never real user speech, so retaining the full text there (not in any diagnostic log) is safe and was explicitly required by §4 of the instruction.

## 8. Fallback results

All 30 fallback cases resolved to the exact baseline text with zero corruption or partial substitution (PROVEN by inspection of the results file — every `FinalText` in a fallback row is byte-identical to that row's `Baseline`). Breakdown of WHY each fell back:

- **28 cases**: `HTTP 429` (rate limit), all 4 attempts exhausted.
- **2 cases** (`D2`, `G2`): `HTTP 503` ("model currently experiencing high demand") — a genuine, if less common, Gemini-side transient failure, correctly handled the same way as any other provider failure (§8 of Step 5.14's original contract: provider failure → baseline).

No case in this run exercised validator-uncertainty fallback, malformed-response fallback, or empty-response fallback — those paths remain PROVEN only via Step 5.14's unit tests (§10), not exercised live in this run because the 8 real Gemini responses that came through were all well-formed.

## 9. Latency measurements

**Two separate latency populations must be reported separately — conflating them would be misleading:**

**(a) Genuine Gemini response latency (T1→T2), the 8 real API calls, in milliseconds:** 2345, 2939, 3299, 1843, 1701, 5317, 6000, 2354.
- n = 8 (too small for a meaningful percentile distribution — **these are OBSERVATIONS, not a benchmark**, per the explicit instruction).
- Median (of these 8): **~2650ms**.
- Max observed: **6000ms** (case D1).
- Min observed: **1701ms** (case C1).

**(b) End-to-end pipeline latency (T0→T3) across all 38 cases, as reported by the live-test harness:** median 256ms, p95 5318ms, max 6000ms. This population is dominated by the 30 fast-failing (~150–500ms) rate-limited cases, so the **median of 256ms is an artifact of the rate limiting, not a meaningful "typical naturalization latency."** The p95/max (5318/6000ms) do reflect the real Gemini-call tail latency and roughly agree with population (a)'s max.

**Conclusion on latency**: real Gemini naturalization adds on the order of **1.7–6 seconds** per segment on top of the baseline Azure translation, based on 8 observations. This is far too slow for the sub-second conversational latency budget this project's other Streaming/ experiments (Steps 5.7–5.13) have been measuring — but it is only 8 samples on the free tier, not a production-tier or streaming-optimized measurement, and should not be treated as a final verdict on Gemini's achievable latency.

## 10. Failure analysis

| Failure mode | Observed in this run? | Evidence |
|---|---|---|
| Rate limit (`HTTP 429`) | **Yes — dominant failure mode** | 28/38 cases, sustained across full retry backoff |
| Transient server error (`HTTP 503`) | Yes | 2/38 cases (D2, G2) |
| Timeout/cancellation | No | 0/38 |
| Empty response | No | 0/38 (not exercised live; PROVEN via unit test, Step 5.14A §10) |
| Malformed response | No | 0/38 (not exercised live; PROVEN via unit test) |
| Provider-signaled uncertainty (refusal/multi-paragraph) | No | 0/38 — Gemini never produced a refusal or off-contract multi-paragraph response in any of the 8 real calls |
| Output-contract violation (explaining itself, answering the content) | No | 0/38 — every real response was a single, direct rephrasing, exactly as instructed |

**Notable positive finding**: in all 8 real responses, Gemini followed the "return ONLY the naturalized text" instruction correctly — no preamble, no "Here is the naturalized text:", no explanation, no answering of embedded content. This is a genuinely good sign for the output-contract enforcement design (§instruction items 13–16), though 8 samples is not enough to call this reliable under adversarial input (no adversarial/prompt-injection corpus case happened to be among the 8 that got a real response this run — see §12 for what WAS and wasn't tested).

## 11. Naturalness observations (HUMAN/QUALITATIVE ASSESSMENT — explicitly not automated)

Reading the 8 real cases directly:

- **A1**: "how are you today?" → "how are you doing today?" — a small, genuine, natural-sounding improvement (HUMAN/QUALITATIVE).
- **B1**: "are you all right?" → "are you okay?" — arguably MORE natural/casual for an informal-conversation category, though "all right" is not wrong either; a judgment call, not an obvious win (HUMAN/QUALITATIVE).
- **C2**: reordering "bitte" — a subtle, genuinely more natural German word order for a polite request (HUMAN/QUALITATIVE), correctly preserved by the validator.
- **E1**: "That's not my beer." → "That's not my problem." — **the single most compelling result in this entire run.** Azure Translator produced a literal, nonsensical translation of the German idiom "Das ist nicht mein Bier"; Gemini correctly recognized and idiomatically naturalized it to the actual intended meaning. This is exactly the kind of improvement naturalization was hypothesized to provide, and it is real, not fabricated (HUMAN/QUALITATIVE, but the underlying text is OBSERVED/real).
- **B2, C1**: Gemini returned the baseline UNCHANGED — a reasonable, conservative choice when the baseline is already natural, not a failure.
- **A2, D1**: see §12 — both REJECTED, but for validator-side reasons, not because the naturalization was actually wrong (see below).

**Overall HUMAN/QUALITATIVE impression from 8 samples**: Gemini's rephrasings, when they changed anything, were consistently plausible, natural, and (as far as a human reading them can tell) meaning-preserving — including one genuine idiom-correction win. This is encouraging but is 8 samples, not a validated general capability.

## 12. Validator blind spots — investigated as explicitly requested

### 12a. Pre-identified blind spots (word-form numbers, entity-role swaps) — not exposed in this run

None of the 8 real cases happened to touch word-form numbers or entity-role swaps (the corpus's number/name/date cases — N, O, P — never got a real Gemini response due to rate-limiting, §6). **This blind-spot category remains UNVALIDATED against real Gemini output** — not because it was checked and found fine, but because the rate limit prevented the relevant corpus cases from ever reaching Gemini.

### 12b. NEW blind spot discovered live: over-rejection on colloquial contraction (case A2)

Case A2's candidate — "Mir geht's gut, danke fürs Nachfragen." — is, to a fluent German reader, an entirely natural, meaning-identical contraction of the baseline "Mir geht es gut, danke der Nachfrage." (`geht es` → `geht's`, `der Nachfrage` → `fürs Nachfragen`, both standard colloquial contractions). The validator REJECTED it purely because the word-level token-overlap ratio (3 shared tokens out of 10 combined distinct tokens = 30%) fell below the 40% `MinTokenCoverageRatio` threshold. **This is a genuine, live-discovered validator limitation: aggressive-but-correct colloquial contraction can push token overlap below the coverage threshold even when meaning is fully preserved**, producing a false REJECTED (over-cautious, not unsafe — the pipeline still safely fell back to the equally-correct baseline, so no harm was done, but a genuinely GOOD naturalization was discarded).

### 12c. NEW validator BUG discovered live: cross-language terminology-list check (case D1)

Case D1's corpus entry supplies BOTH the German and English spellings of its technical term (`TerminologyTerms: ["Nullzeiger-Ausnahme", "null pointer exception"]`) — reflecting that the SAME corpus case object describes one term across the translation direction. `MeaningPreservationValidator.TerminologyPreserved` checks that **every** listed term string appears in the candidate text, unconditionally — it does **not** first check whether that term actually appeared in the baseline for this specific direction. Since D1 is de→en, the baseline (and therefore any legitimate candidate) is in English and will never contain the German spelling "Nullzeiger-Ausnahme" — so the check is **structurally guaranteed to fail** for this case regardless of what Gemini returns, even though Gemini's candidate ("The server is throwing a null pointer exception...") correctly preserves "null pointer exception" verbatim. **This is a confirmed validator defect, not a Gemini quality issue** — it would have rejected ANY candidate for this case, including a perfect one. Per the explicit instruction NOT to silently expand/fix the validator inline, this is reported here as a finding only. **Recommended fix (not applied in this step): `TerminologyPreserved` should only require terms that actually appear (case-insensitive) in the BASELINE text, since a term listed for the source-language direction is not expected to appear in a target-language candidate.** This should be implemented and tested independently, per instruction.

### 12d. "No new information" guarantee — still unverified

As already documented in the base Step 5.14 document, no dedicated check exists for fabricated additions using only already-present vocabulary. None of the 8 real cases exhibited an obvious addition, but 8 cases is far too small a sample, and this category of error is specifically the kind that would slip past every existing check (it doesn't move a number, name, date, negation, or question mark, and may not even move token overlap much). **This remains the most significant open validation gap, UNVALIDATED either way by this run.**

## 13. Security / secret-scan result

- **No API credentials in source**: `grep -rEi "AIza[A-Za-z0-9_-]{20,}"` across `src/`, `tools/`, `tests/`, `docs/`, and `test-results/` → **no matches** (PROVEN — this pattern matches the actual Gemini key format and was run AFTER the live experiment, confirming the real key used in this run never leaked anywhere, including the results TSV that DOES contain corpus text).
- **No credentials in logs**: the metadata-only diagnostic log (`translation-naturalization-gemini.log`) contains only `utteranceId`/`generation`/`segmentSequence`/`baselineLength`/outcome enums — verified by the same unit test guarantee as base Step 5.14 (`NeverLogsBaselineOrCandidateText_OnlyMetadata`), which covers the unmodified orchestrator this run reused.
- **No source/translation text in diagnostic logs**: confirmed — the FileDiagnosticLogger output contains zero text content; all text lives only in the separate, explicitly-requested results TSV (§7 note).
- **No persistent conversation memory introduced**: `GeminiNaturalizationProvider` holds no state between calls beyond its constructor-injected `HttpClient`/API key/model ID — confirmed by inspection, no field stores prior request/response text.
- **No unrestricted learning introduced**: nothing in this step adapts behavior based on past results; each corpus case is processed independently.

## 14. Complete test result

`dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj` → **392/392 passed** (375 from Steps 1–5.14 + 17 new `GeminiNaturalizationProviderTests`), zero regressions. The 17 new tests cover: missing API key, successful response, empty candidates array, blocked-by-safety response, malformed JSON, missing content parts, HTTP error status (with proof the response body — including a fake leaked key string — never reaches the failure reason), safety `finishReason`, network exception, timeout (propagates as cancellation for the orchestrator to handle), refusal-language output-contract violation, multi-paragraph output-contract violation, prompt-injection posture (malicious baseline text sent as data, not as an instruction override), and two dedicated no-credential-leakage tests. All use a fake, in-memory `HttpMessageHandler` — zero real network calls in the unit-test suite itself (only the live-test command makes real calls).

## 15. Build result

`dotnet build VTTranslate.sln` → **0 errors, 0 warnings** across all four projects (`VTTranslate.Core`, `VTTranslate.App`, `VTTranslate.Core.Tests`, `VTTranslate.LiveTest`) — PROVEN, re-run after the live experiment completed.

## 16. Production isolation

- `AzureSpeechTranslationProvider.cs` — **untouched** (`grep -c "Gemini\|Naturalization"` → `0`; no diff in this step).
- `DirectionPipeline.cs` — **untouched** (`grep -c "Gemini\|Naturalization"` → `0`).
- Production UI (`VTTranslate.App/*.cs`) — **untouched**, zero references.
- `GeminiNaturalizationProvider` is never constructed anywhere except the isolated `translation-naturalization-gemini-test` live-test command and its own unit tests — not referenced by any production type.
- Not connected to microphone capture, live ASR, live TTS, live audio playback, Google Meet, or production UI — confirmed by inspection (no `using` for any of those namespaces in the new files).

## 17. Final decision (§13 of the instruction)

1. **Did naturalization improve naturalness?** In the 8 cases where Gemini actually responded: yes, in at least 5 of 8 (A1, B1, C2, E1 clearly; B2/C1 conservatively unchanged, which is also a reasonable outcome, not a failure) — **HUMAN/QUALITATIVE assessment**, not an automated naturalness score (none exists).
2. **By how much / in how many cases?** 6 of 8 real responses were accepted as `SafeToUse` (75% of the cases Gemini actually got to attempt) — but this is **8 samples**, and 30 of 38 total corpus cases never got a real attempt due to rate limiting. The true acceptance rate across the full, intended 38-case corpus is **UNVALIDATED**.
3. **Did it introduce semantic errors?** No confirmed semantic error was found in any of the 8 real candidates by either the validator or human reading. The two REJECTED cases (A2, D1) were both, on inspection, **validator false-positives** (§12b, §12c), not genuine semantic errors in Gemini's output — a notable and important finding: in this small sample, the validator was MORE conservative than necessary, not less.
4. **What was the fallback rate?** 30/38 (79%) overall, but 28 of those 30 are rate-limit artifacts, not naturalization outcomes. Among genuine attempts, the fallback rate (validator-driven) was 2/8 (25%) — and both of those, per §12, appear to be validator over-caution rather than real problems with Gemini's output.
5. **What was the additional latency?** 1.7–6 seconds per segment (8 observations, median ~2.65s) — see §9's caveats. Far above this project's conversational latency targets from Steps 5.7–5.13, based on a small free-tier sample.
6. **Did German → English and English → German behave similarly?** Of the 8 real responses, 5 were de→en (A1, B1, C1, D1, E1) and 3 were en→de (A2, B2, C2). de→en: 4 accepted, 1 rejected (D1). en→de: 2 accepted, 1 rejected (A2). Both directions produced both accepted and rejected outcomes; no strong directional asymmetry is visible in this small sample, but 8 cases split across two directions is **too small to draw a reliable directional conclusion** — UNVALIDATED.
7. **Are the current safety checks sufficient?** The FALLBACK MECHANICS remain PROVEN sufficient (no unsafe text was ever delivered — every fallback and every rejection correctly returned to the exact baseline). The VALIDATOR's checks, however, were shown in this very run to be **over-conservative in at least two concrete, reproducible ways** (§12b contraction false-positive, §12c terminology-list bug) — "sufficient" for SAFETY (never delivers something wrong) but demonstrably **not yet well-calibrated** (rejects some genuinely fine candidates).
8. **Is the naturalization layer safe enough for a controlled end-to-end experiment?** Yes, for SAFETY specifically — nothing in this run delivered an unsafe or corrupted result. Not yet ready to trust the ACCEPTANCE RATE as representative, given the small real sample and the two discovered validator issues.
9. **Should we ADOPT, REFINE, or REJECT it?** **REFINE.** Concretely, in priority order: (a) fix the confirmed terminology-check bug (§12c) — baseline-presence should gate the check, not an unconditional list; (b) reconsider whether `MinTokenCoverageRatio` (40%) is well-calibrated for legitimate colloquial contractions, informed by more real samples (§12b); (c) obtain a paid/higher-quota Gemini tier (or a different, non-rate-limited provider) to re-run the full 38-case corpus and get a real, complete acceptance-rate and directional comparison; (d) investigate real streaming/lower-latency Gemini options if latency (§9, §17.5) remains in the multi-second range, since that is incompatible with this project's established conversational-latency goals. **Do not adopt as-is**, and do not reject either — genuine quality signal (the idiom fix) and safety-mechanism correctness both survived this test.

## Evidence artifacts

- Per-case results (source/baseline/candidate text, hand-authored corpus, not real user speech): `test-results/logs/translation-naturalization-gemini/gemini-naturalization-results.tsv`
- Metadata-only diagnostic log: `test-results/logs/translation-naturalization-gemini/translation-naturalization-gemini.log`
- New source: `src/VTTranslate.Core/Streaming/GeminiNaturalizationProvider.cs`
- New tests: `tests/VTTranslate.Core.Tests/GeminiNaturalizationProviderTests.cs`
- New live-test command: `translation-naturalization-gemini-test` in `tools/VTTranslate.LiveTest/Program.cs`

**STOP — Step 5.14A is complete. Waiting for review. Do not integrate into production, start Google Meet, mobile, self-learning, persistent adaptive memory, voice cloning, or Step 5.15 without explicit authorization.**
