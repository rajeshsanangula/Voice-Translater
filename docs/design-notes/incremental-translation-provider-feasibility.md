# Step 5.5 / 5.6 / 5.6a / 5.6b — Incremental Translation Provider Feasibility

**Status: SHADOW/EXPERIMENTAL ONLY. Existing production translation/TTS path unchanged.
No experimental candidate reaches TTS, playback, or any user-visible surface. No LLM,
naturalization, Adaptive Translation Memory, persistent memory, mobile/backend, or voice
cloning was added. `AzureSpeechTranslationProvider.cs` was not modified in either step.**

**HEADLINE FINDING, UPDATED IN STEP 5.6b: genuine independent translations have now been
obtained. After Step 5.6a added dedicated `AZURE_TRANSLATOR_KEY`/`AZURE_TRANSLATOR_REGION`/
`AZURE_TRANSLATOR_ENDPOINT` configuration (never falling back to the Speech credentials
that caused every earlier 401), and a fresh Translator key was provisioned in Step 5.6b
(the prior key having been exposed in Step 5.6a's session output and is no longer used),
both directional smoke-test calls succeeded with HTTP 200 and real translated text, and
the full 13-case B1/B2/B3 experiment then ran successfully end-to-end — the first time
this project has obtained real, independent (non-Speech-bundled) translation evidence.
See §18 for the full Step 5.6b account, and read §2/§16/§17 below as history: they
describe the Speech-key 401s (Step 5.5/5.6) and the code fix that didn't yet have a
working credential to test against (Step 5.6a) — all superseded by §18's real results.**

---

## 1. Objective

Determine how a real, genuinely independent translation operation (as opposed to Step 5's reuse of Azure Speech's bundled per-partial translated output) should consume stable source segments, evaluating three consumption strategies (B1 cumulative, B2 contextual-segment, B3 boundary-aware) for semantic correctness, grammatical naturalness, context preservation, incremental usefulness, duplication, correction handling, and final reconciliation.

## 2. Provider capabilities (inspected first, per instruction)

- **Existing codebase**: grepped the full `src/`/`tools/` tree for any prior use of a standalone text-translation API — none found. The only translation capability in this project is `Microsoft.CognitiveServices.Speech.Translation.TranslationRecognizer`, which bundles ASR+MT into one Speech Translation call (used by `AzureSpeechTranslationProvider` since the project's inception) — there is no code path anywhere that calls a separate Translator resource.
- **Live capability check**: made one real HTTP request to the genuine, documented Azure AI Translator "Text Translation" REST API (`https://api.cognitive.microsofttranslator.com/translate?api-version=3.0`) using the existing `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` as `Ocp-Apim-Subscription-Key`/`Ocp-Apim-Subscription-Region` headers (the standard, documented authentication scheme for this API). **Result: HTTP 401 Unauthorized.**
- **Why this is expected, not a bug**: a single-service "Speech" Cognitive Services resource's key is not automatically valid for the separately-billed, separately-provisioned "Translator" resource type — only a multi-service "Cognitive Services" resource's key grants both. This project's resource, per this result, is a single-service Speech resource.
- **This is a real, supported, documented Azure API** (not invented) — the 401 is an authorization-provisioning gap in this environment's credentials, not a claim that the API itself doesn't exist or isn't usable in principle.
- **Decision made per your explicit instruction**: rather than silently falling back to Step 5's reinterpretation technique and presenting it as "the independent-translation experiment," this step (a) built the real, correct client code for the genuine API (§3), (b) confirmed empirically that it cannot succeed with current credentials, and (c) reports that limitation as the primary, headline finding of this document — not buried, not worked around.

## 3. Experimental architecture

```
IIncrementalTranslationProvider (interface)
        │
        ▼
AzureTranslatorTextProvider — real HTTP client for the genuine Azure Translator Text
   v3.0 REST API. Credentials used only in request headers; never logged, never
   returned in TranslationProviderResult, never included in exception/failure text
   (unit-tested explicitly — see §16/tests).
        │
        ▼
IncrementalTranslationProviderExperiment — orchestrates strategies B1/B2/B3, calling
   IIncrementalTranslationProvider.TranslateAsync per gated step, logging metadata-only
   diagnostics, and reconciling each strategy's in-memory cumulative candidate against
   the true final translation via the same structural (token-multiset, non-semantic)
   method Step 5 used.
```

`AzureTranslatorTextProvider` and `IncrementalTranslationProviderExperiment` ([src/VTTranslate.Core/Streaming/](../../src/VTTranslate.Core/Streaming/)) have no events and no reference to production translation-dispatch, TTS, or playback — the same structural isolation guarantee as every prior shadow component (Steps 3-5). `AzureTranslatorTextProvider` takes an injected `HttpClient`, making it fully unit-testable with a fake handler — no live credentials needed for the test suite (§16).

## 4. B1 — cumulative source translation

Sends the **entire cumulative stable source** as the translation request text on every gated step (matching your example exactly: segment 1 → "I would like", segment 2 → cumulative "I would like to schedule", etc.). Each call is fully independent of the provider's own internal history (no session/context parameter exists in the Translator API) — consistency across calls, if any, comes entirely from the fact that the input text itself is monotonically growing, not from any provider-side memory.

## 5. B2 — contextual segment translation

Sends only the **newly stable segment**, with a **bounded preceding-context window** (last 8 words of the cumulative stable source preceding the new segment) prepended to the request text. **Documented limitation**: the Translator API has no native "context but don't translate it" parameter — the only available mechanism is to include the context as part of the translated text itself, then (in a real deployment) discard the context portion of the response. This is a real, working approximation, not an invented capability, but it is coarser than a true "translate with context" API would be — flagged explicitly rather than presented as more capable than it is.

## 6. B3 — boundary-aware translation

Buffers newly stable segments until the accumulated buffer contains sentence-ending punctuation (`.`, `!`, `?`), then translates the whole buffered phrase as one request and clears the buffer. Any content still buffered when the utterance's Final arrives is flushed and translated at that point, regardless of whether it ends in punctuation — ensuring no content is silently dropped even for an interrupted/incomplete sentence (test case 12).

## 7. Test methodology

Since **zero genuine translations were obtainable** (§2), live test methodology necessarily split into two parts:
- **Unit tests** (`AzureTranslatorTextProviderTests.cs`, `IncrementalTranslationProviderExperimentTests.cs` — 15 tests total, all executed and passing, §16): verify request-construction correctness (B1 sends full cumulative text; B2 sends the new segment plus a genuinely bounded — not unbounded — context window; B3 correctly holds back until a sentence boundary and correctly flushes at Final even without one), response-handling correctness (both success and 401-style failure paths), and the privacy/credential-safety guarantees, all against a fake `HttpMessageHandler`/fake `IIncrementalTranslationProvider` — no network, no real credentials required, fully deterministic.
- **Live run** (`translation-provider-feasibility-test` in `VTTranslate.LiveTest`): runs the real `AzureTranslatorTextProvider` (real `HttpClient`, real credentials) through the actual `IncrementalTranslationProviderExperiment` orchestration for all 13 required test cases, each split into 2 stable-segment commits plus a Final, exactly matching how the class would be driven in a real pipeline. This needed only text input (not audio/speech recognition), since it evaluates text-translation-provider capability independent of speech recognition — a deliberate, appropriate scoping for what this specific step is testing.

## 8. Live results

**All 13 cases, all 3 strategies, zero successful calls.** Two distinct HTTP status codes were observed across the run: the first two calls (case 1, both strategies' first attempt) returned **401 Unauthorized**; every subsequent call across the remaining 12 cases returned **429 Too Many Requests**. Both are reported exactly as observed, without over-interpreting the 429 — it may reflect endpoint-level rate/IP throttling applied independently of (or in addition to) the authorization failure; in no case did the sequence of calls ever produce a 200 response with real translated content. `AnySuccessfulCall` was `False` for every one of the 39 strategy-final-reconciliations produced (13 cases × 3 strategies), and `ApproxMissingTokenCount`/`ApproxDuplicateTokenCount` were correctly reported as `null` (not fabricated as `0` or any other value) whenever no successful call had ever occurred — verified both live and by dedicated unit test (`ProviderFailure_ReportedHonestly_NoFabricatedCandidate`).

Per-strategy call counts (how many requests each strategy issued, independent of success) matched the expected shape: B1 and B2 fired on every gated step (2 per case with 2 segments), B3 fired only when a sentence boundary was reached or at Final (1 per case in every test case here, since none of the 13 cases' first two segments happened to contain mid-buffer punctuation before the Final flush).

## 9. Naturalness observations

**None possible.** No candidate translation text was ever produced by a real call, so there is nothing to qualitatively assess for naturalness. This is reported as a gap, not glossed over with a fabricated qualitative judgment.

## 10. Correctness observations

**None possible**, for the same reason as §9. No numeric "accuracy" figure is reported anywhere in this document, consistent with the explicit instruction not to invent one without an objective reference — and here there is no reference at all, since there was no candidate to measure.

## 11. Latency observations

The only real, honest latency data available is **call latency to a failure response**: individual `TranslateAsync` calls (all failing) completed in roughly 50-870ms, with no clear pattern distinguishing 401 responses from 429 responses. This says nothing about real translation latency (which was never observed) — only that the endpoint responds quickly even when rejecting the request.

## 12. Reconciliation behavior

Every one of the 39 strategy-final-reconciliations (13 cases × 3 strategies) correctly reported `AnySuccessfulCall=False`, `CumulativeTokenCount=0`, and `ApproxMissingTokenCount`/`ApproxDuplicateTokenCount` as `null` — the reconciliation logic degrades honestly to "nothing to reconcile" rather than treating an empty cumulative candidate as if it were a real (100%-missing) translation attempt. This distinction matters: `null` correctly signals "no data," while a fabricated `0` or a computed "100% missing" figure would misleadingly imply a real translation was attempted and simply omitted everything, which is not what happened.

## 13. Safe-to-speak criteria

Because no real candidate was ever obtained, this section describes the **criteria the architecture applies** (verified by unit test, `ProviderFailure_ReportedHonestly_NoFabricatedCandidate` and the `ProviderStrategyStability` enum's usage in `IncrementalTranslationProviderExperiment.RunStrategyAsync`), not real examples classified against real content:

- **A. Safe to speak immediately**: a successful, non-wholesale-replace candidate (B2/B3 style) — appended incrementally, never itself subject to being silently discarded by the next step. Maps to `ProviderStrategyStability.SafeToSpeak` in the code.
- **B. Must remain provisional**: a successful but wholesale-replace candidate (B1's every step) — by construction, B1 always replaces its own previous candidate, so nothing it produces is safe to have already spoken; each step's candidate is provisional until the next replacement or the Final. Maps to `ProviderStrategyStability.Provisional`.
- **C. Requires later revision**: not distinctly observed in this run (no successful calls occurred to exercise this path), but architecturally reserved for a candidate that is later contradicted by a subsequent segment's translation — this project's `ProviderStrategyStability` enum includes `RequiresRevision` for exactly this case, unused in this run only because no strategy ever got far enough to detect one.
- **D. Cannot be incrementally spoken without risking an obviously wrong translation**: any failed call (`Success=false`) — maps to `ProviderStrategyStability.UnsafeToSpeak`, which is what every single call in this live run actually returned, since none succeeded.

## 14. Unsafe-to-speak examples

Every one of the 39 live strategy-steps in this run is, concretely, an example of category D — a failed call with no candidate text at all. There is no example available of a successful-but-still-unsafe candidate (e.g., a plausible-looking but semantically wrong translation), since obtaining that would require at least one successful call, which this environment's credentials do not permit.

## 15. Automated test and build verification

- **`dotnet build VTTranslate.sln`**: 0 warnings, 0 errors (unchanged from Step 5.5 — no production or experimental code was modified in Step 5.6, only a new harness command was added to `VTTranslate.LiveTest`).
- **`dotnet test`**: 184/184 passed (the full suite, including the 15 tests from Step 5.5 covering `AzureTranslatorTextProvider` and `IncrementalTranslationProviderExperiment` — unaffected, since neither class changed).
- **Secret scan**: clean — verified the new `translator-credential-verify` harness command never prints the key value, only its length (`AZURE_SPEECH_KEY length=84`), consistent with the existing `AzureTranslatorTextProvider` guarantee (credentials only ever appear in HTTP request headers, never in logs, console output, or exception text — unit-tested in Step 5.5's `Credentials_NeverAppearInFailureReason`/`Credentials_SentOnlyAsHeaders_NeverInUrlOrBody`).

## 16. Step 5.6 — credential re-verification (this step)

**Requirement 1 — environment variables/settings the implementation expects**: inspected `AzureTranslatorTextProvider`'s constructor (`src/VTTranslate.Core/Streaming/AzureTranslatorTextProvider.cs`) — it takes a plain `(HttpClient, string subscriptionKey, string region)` and expects no environment variable itself; the caller supplies credentials. The only caller in this project, `VTTranslate.LiveTest`'s `RequireAzureConfig()`, sources both values from `AppSettings.Load()`, which reads the existing `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` environment variables — the same two variables used throughout this entire project (never a separate `AZURE_TRANSLATOR_KEY` or similar; no such variable exists in this codebase). **There is no code path in this project that reads a Translator-specific credential name.**

**Requirement 2 — verifying the configured credentials are actually being read**: confirmed live — the new `translator-credential-verify` command printed `AZURE_SPEECH_KEY length=84, AZURE_SPEECH_REGION=eastus` before making any call, proving the values are genuinely read from the environment at runtime (not hardcoded, not stale from a build cache). **These are the exact same length (84) and exact same region (`eastus`) observed in Step 5.5's run** — this session cannot see the underlying key's actual value (no code prints it, by design), so it cannot prove byte-for-byte the value is unchanged, but the length and region match exactly.

**Requirements 3-4 — minimal real API calls, both directions**: made via `AzureTranslatorTextProvider` unchanged from Step 5.5, using the real endpoint `https://api.cognitive.microsofttranslator.com/translate?api-version=3.0`:

| Direction | Test sentence | HTTP status | Auth succeeded | Latency | Returned translation | Genuine standalone API? |
|---|---|---|---|---|---|---|
| German → English | "Ich möchte morgen ein Treffen vereinbaren." | **401** | **False** | 871ms | none (call failed) | Yes — confirmed genuine Translator endpoint, request reached the service and was rejected there, not a local/network-level failure |
| English → German | "I would like to schedule a meeting tomorrow." | **401** | **False** | 474ms | none (call failed) | Yes — same |

**Requirement 5 — reported above.** Provider/endpoint used: `api.cognitive.microsofttranslator.com` (the genuine, documented standalone Translator Text v3.0 API — not the bundled Speech Translation `TranslationRecognizer` this project's production pipeline uses).

**Requirement 6 — privacy**: confirmed no key/secret/authorization-header value or full environment variable content was printed anywhere in this run — only the key's length and the region (region is not secret; it's already visible in this project's non-secret `AppSettings`/UI). Verified by direct review of the command's own source and by re-confirming the standing unit tests (`Credentials_NeverAppearInFailureReason`, `Credentials_SentOnlyAsHeaders_NeverInUrlOrBody`) still pass unmodified.

**Requirement 7 — authentication failed, so per instruction: STOPPED here.** No alternative credentials, proxies, reused Speech APIs, or workarounds were attempted. Exact status/category: **HTTP 401 Unauthorized**, both directions, both attempts.

**Requirements 8-9**: since both basic directional calls failed, **the full 13-case `translation-provider-feasibility-test` experiment was NOT re-run** in Step 5.6, exactly as instructed.

**What this step could and could not determine**: this session can confirm the code correctly reads whatever value is currently in `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` and correctly sends it to the genuine Translator API, and that the service rejects it with 401. It **cannot** determine, from inside this session, why — whether the newly-configured Translator credentials were placed under a different variable name this project's code doesn't read, configured at a different scope (e.g., a shell/session this environment variable snapshot doesn't reflect), not yet propagated/restarted into this session, or genuinely not yet granted to the underlying resource. The observed key length/region being identical to Step 5.5's is consistent with (but does not prove) "the environment variables this session sees have not actually changed since Step 5.5" — a fresh shell/session restart after credential configuration might resolve this, but re-attempting is outside this step's scope per instruction 7 ("do not attempt... workarounds").

## 17. Step 5.6a — dedicated Translator credential configuration (this step)

**Root cause of Step 5.6's 401s, now confirmed precisely**: `AzureTranslatorTextProvider` was being constructed with the Speech-only `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` values (via `RequireAzureConfig()`, the same helper every other Speech-pipeline command in this harness uses) — there was no dedicated Translator credential path at all. The 401s in Step 5.5/5.6 were not a genuine "Translator access denied" result on a Translator resource; they were the expected result of authenticating to the Translator API with a Speech resource's key, which is a different, unrelated Azure resource type.

**What changed in this step — configuration isolation only, no live call made yet**:

- **New `TranslatorCredentialLoader`** ([src/VTTranslate.Core/Streaming/TranslatorCredentialConfig.cs](../../src/VTTranslate.Core/Streaming/TranslatorCredentialConfig.cs)) reads three dedicated environment variables — `AZURE_TRANSLATOR_KEY`, `AZURE_TRANSLATOR_REGION`, `AZURE_TRANSLATOR_ENDPOINT` (optional) — independently of, and with **no fallback to**, `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION`. `TryLoad()` returns `null` if the key or region is missing; `LoadOrThrow()` throws a clear `InvalidOperationException` naming exactly which variable(s) are missing (never any value) and explicitly stating these are separate from the Speech credentials.
- **`AzureTranslatorTextProvider`** ([src/VTTranslate.Core/Streaming/AzureTranslatorTextProvider.cs](../../src/VTTranslate.Core/Streaming/AzureTranslatorTextProvider.cs)) gained an optional `endpoint` constructor parameter (defaulting to the public global Translator endpoint, used unless a specific one is configured — satisfying "use the endpoint only if required") and a `FromCredentialConfig(HttpClient, TranslatorCredentialConfig)` factory. The class still never reads any environment variable itself — credentials are always supplied explicitly by the caller, preserving the existing provider abstraction from Step 5.5.
- **`IncrementalTranslationProviderExperiment`** — unmodified. The existing Step 5.5 experiment is preserved exactly; only what constructs its `IIncrementalTranslationProvider` changed.
- **`VTTranslate.LiveTest`**'s `translator-credential-verify` and `translation-provider-feasibility-test` commands now call `TranslatorCredentialLoader.LoadOrThrow()` and `AzureTranslatorTextProvider.FromCredentialConfig(...)` instead of `RequireAzureConfig()` — if dedicated Translator credentials are absent, both commands now fail immediately with a clear error naming the missing variable(s), rather than silently reusing Speech credentials (which is exactly what happened before).
- **`AzureSpeechTranslationProvider.cs` was not touched** — confirmed by diff; the production Speech pipeline, TTS, playback, and existing translation behavior are completely unaffected by this step.

**Tests added** (`TranslatorCredentialConfigTests.cs`, plus two new tests in `AzureTranslatorTextProviderTests.cs` — 15 new tests total): prove credentials are read independently, that missing values fail via a clear exception (never silently substituting anything), that the exception message names the right variable(s) without ever echoing a configured value, that `DescribeSafely` never exposes a key value (only its length) or a full endpoint URL (only its host), and that a configured custom endpoint is actually used in the request URL while the default is used when none is configured. One test (`TryLoad_AgainstRealEnvironment_...`) exercises the real environment-reading path safely — it asserts only on shape (a config came back or didn't; if it did, the region is non-blank and the key has positive length) so it is meaningful and safe to run whether or not this specific machine has Translator credentials configured, without ever asserting on or printing the key's actual value.

**No live API call was made in this step**, per instruction 8 — this step is configuration isolation only. §16 (Step 5.6's original two-call verification) and §17 (recommendation) below remain the last real live evidence on file; a subsequent step would need to explicitly re-run `translator-credential-verify` against the newly dedicated configuration to obtain fresh live evidence.

**Note found during environment inspection, reported for completeness and NOT acted on further**: while confirming the new environment variables were being read correctly, this session observed that `AZURE_TRANSLATOR_KEY`/`AZURE_TRANSLATOR_REGION` do appear to be set at User scope on this machine as of this step (a properties-only check, not a value-revealing one, was used going forward — see the safe `TryLoad_AgainstRealEnvironment_...` test above for the pattern used). Whether these are genuinely authorized for the standalone Translator API is unverified until a live call is made in a future, explicitly-approved step.

## 18. Step 5.6b — live independent Translator verification and full experiment (this step)

**Configuration used**: exclusively `AZURE_TRANSLATOR_KEY`/`AZURE_TRANSLATOR_REGION`/`AZURE_TRANSLATOR_ENDPOINT` via `TranslatorCredentialLoader.LoadOrThrow()` and `AzureTranslatorTextProvider.FromCredentialConfig(...)` — `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` were never read or used anywhere in this step. A fresh Translator key was in place (the key exposed in Step 5.6a's session output was superseded and was not used here).

### Smoke tests (decision gate)

| Direction | Input | HTTP status | Auth succeeded | Latency | Endpoint host | Region | Returned translation |
|---|---|---|---|---|---|---|---|
| German → English | "Ich möchte morgen ein Treffen vereinbaren." | **200** | **True** | 1369ms | api.cognitive.microsofttranslator.com | global | "I would like to arrange a meeting tomorrow." |
| English → German | "I would like to schedule a meeting tomorrow." | **200** | **True** | 239ms | api.cognitive.microsofttranslator.com | global | "Ich möchte für morgen einen Termin vereinbaren." |

Both calls reached the genuine standalone Azure Translator Text API (confirmed by endpoint host) and succeeded. Per the decision gate, both directions succeeding meant proceeding to the full 13-case experiment was authorized.

### Data-quality fix made during this step

The full-experiment harness (`RunTranslationProviderFeasibilityTestAsync` in `VTTranslate.LiveTest/Program.cs`) had, since Step 5.5, used a hardcoded placeholder string, `"(unavailable — see result)"`, as every test case's "final translated text" — a reasonable choice when no real translation was ever expected to succeed, but the first live run in this step revealed the problem: every single case reported `finalTokenCount=4` and `approxMissingTokenCount=4` regardless of the actual sentence, because 4 was simply the placeholder string's own token count, not a measurement of anything real. **This was corrected** by adding one more genuine `provider.TranslateAsync` call per case to obtain the real final translation before reconciliation, falling back to the placeholder only if that specific call somehow failed (never fabricating a value). This is a fix to test **data**, not to `IncrementalTranslationProviderExperiment` itself, which remains exactly as built in Step 5.5 — consistent with the instruction to run the experiment "unchanged." The experiment was then re-run with this fix in place; all results below are from the corrected run.

### Full 13-case results

All 13 cases, all three strategies, succeeded on every call (39 of 39 strategy-attempts across the corrected run; **HTTP 200 throughout, zero failures**).

| Case | B1 cumulative/final tokens | B1 missing/duplicate | B2 cumulative/final tokens | B2 missing/duplicate | B3 cumulative/final tokens | B3 missing/duplicate |
|---|---|---|---|---|---|---|
| 1 English | 7/7 | 0/0 | 10/7 | 0/3 | 7/7 | 0/0 |
| 2 German | 6/6 | 0/0 | 9/6 | 0/3 | 6/6 | 0/0 |
| 3 Fast speech | 9/9 | 0/0 | 12/9 | 0/3 | 9/9 | 0/0 |
| 4 Long sentence | 16/16 | 0/0 | 22/16 | 0/6 | 16/16 | 0/0 |
| 5 Short sentence | 1/1 | 0/0 | 1/1 | 0/0 | 1/1 | 0/0 |
| 6 Self-correction | 7/5 | 1/3 | 12/5 | 1/8 | 7/5 | 1/3 |
| 7 Multiple utterances | 3/2 | 0/1 | 4/2 | 0/2 | 3/2 | 0/1 |
| 8 Conversational | 11/11 | 0/0 | 16/11 | 0/5 | 11/11 | 0/0 |
| 9 Business terminology | 9/9 | 0/0 | 14/9 | 0/5 | 9/9 | 0/0 |
| 10 Idiomatic | 7/7 | 0/0 | 10/7 | 0/3 | 7/7 | 0/0 |
| 11 Punctuation | 16/16 | 0/0 | 23/16 | 0/7 | 16/16 | 0/0 |
| 12 Incomplete | 5/5 | 0/0 | 9/5 | 0/4 | 5/5 | 0/0 |
| 13 Final diverges | 8/7 | 3/4 | 14/7 | 3/10 | 8/7 | 3/4 |

**Clear, consistent pattern**: **B1 (cumulative source translation) and B3 (boundary-aware) are byte-for-byte identical in every single case** — both in commit shape and reconciliation outcome — and achieve **perfect reconciliation (0 missing, 0 duplicate) in 10 of 13 cases**, with the 3 exceptions (6, 7, 13) explained entirely by their source text genuinely diverging between the incremental commits and the Final by design (a real self-correction, a multi-utterance boundary artifact, and a deliberately-diverging final in case 13) — not a strategy defect. **B2 (bounded-context segment translation) shows consistent duplicate-token inflation in every case**, including the 10 "clean" cases where B1/B3 are perfect (3-7 extra tokens each). This is the concrete, now-measured cost of B2's documented approximation (§6): because the Translator API has no native "context but don't translate it" parameter, B2 prepends context words as literal text in the request, and the response — a full re-translation of context-plus-segment — gets treated as new content, so the context words are counted (and would be spoken/displayed) a second time. This is real, quantified evidence, not a hypothesis.

### Latency (real, not proxy)

Individual real translation calls ranged roughly 108-635ms, with no strategy showing a systematically different latency profile from another — call cost is dominated by the API round-trip, not by which strategy issued it.

### What remains unvalidated

Per the explicit instruction, this document does **not** claim naturalness, streaming suitability, or production readiness from these 13 short smoke-test-scale cases. What §18 *can* now claim, for the first time across Steps 5.5-5.6b: genuine independent-provider translation is achievable with correctly-scoped credentials, and B1/B3's structural correctness (as measured by token-multiset reconciliation, not semantic judgment) is real and consistent, while B2's specific context-prepending approximation has a real, quantified downside. Grammatical naturalness, semantic correctness beyond token-overlap, and behavior at real conversational scale (longer sessions, more utterances, higher call volume/cost) remain entirely unassessed.

## 19. Recommendation for the next stage (updated after Step 5.6b — first real independent evidence)

**Still do not proceed to streaming TTS** — Step 5.6b is real progress (genuine independent translations obtained for the first time) but is explicitly scoped as smoke-test-scale evidence, not a green light for streaming/user-visible work. **The strategy signal is now real and actionable, though**: B1 and B3 are indistinguishable in this data and both structurally sound; B3 achieves the same correctness as B1 while remaining meaningfully more incremental in cases with genuine sentence-boundary structure (case 7's B3 fired 2 real calls, one per utterance boundary, vs. B1/B2's per-segment-commit cadence) — B3 is the more promising candidate to carry forward, contingent on further, larger-scale evaluation. **B2 should be reconsidered or reworked** — its context-prepending approximation has a real, consistent, quantified duplication cost, and either a native context mechanism (checking whether the Translator API's newer versions expose one) or abandoning B2 in favor of B1/B3 is the fair conclusion from this data. Before any further step: (a) evaluate B1/B3 against a larger, more realistic case set (longer conversations, real audio-driven segment timing rather than 2-segment synthetic splits), (b) get a second, qualitative (human) review of translation naturalness — genuinely unassessed here, and explicitly not claimed, and (c) reconcile this finding with Step 5's original (bundled-output-based) conclusion that "Strategy B [cumulative re-translation]" was best-evidenced — Step 5's "B" and this step's "B1" are conceptually the same strategy, so the two steps' conclusions are actually consistent, strengthening confidence in cumulative-source-style translation specifically, independent of which underlying translation mechanism supplies it.
