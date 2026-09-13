# Step 5.12 — Streaming TTS Feasibility Experiment

**FROZEN — UNVALIDATED. By explicit instruction, this experiment is frozen as of the
re-run attempt below: no further WDAC recovery attempts, no further live-execution
attempts, and no implementation changes are to be made until it is validated in a
separately authorized execution environment. Step 5.13 has not been started. Everything
in this document remains exactly as produced across the original Step 5.12 pass and its
one re-run attempt — nothing below was altered to reach this freeze; only this notice and
the re-run addendum (end of document) were added.**

**Status: SHADOW/EXPERIMENTAL ONLY. `AzureSpeechTranslationProvider.cs` was not modified
(0 references to any Step 5.12 type — confirmed by grep, §16). No production audio path
invokes this experiment. No Google Meet routing is involved. No physical audio output
device was used at any point — the isolated test playback sink is purely in-memory.**

**HEADLINE FINDING, stated up front, honestly: this environment's Windows Application
Control (WDAC) policy blocked BOTH `dotnet test` and the live `VTTranslate.LiveTest`
console executable for the entire duration of this step, despite multiple full clean
rebuilds, process kills, and retries — the same class of intermittent block documented
repeatedly earlier in this project's session. `VTTranslate.App.exe`, built from the
identical `VTTranslate.Core.dll`, continued to load and run normally throughout,
confirming this is an executable/host-specific enforcement decision, not a defect in the
code produced this step. No bypass of WDAC was attempted, per this project's standing
rule. As a direct consequence: no fresh live TTS calls and no executed unit-test run were
obtained THIS STEP — every quantitative TTS-latency/behavior claim below is either
labeled **UNVALIDATED THIS STEP** or is drawn from this project's own, already-established
historical evidence (clearly cited as such, never presented as new data).**

---

## 1. Provider abstraction

**PROVEN (by code review — build succeeded)**: `IStreamingTtsProvider` ([src/VTTranslate.Core/Streaming/StreamingTtsModels.cs](../../src/VTTranslate.Core/Streaming/StreamingTtsModels.cs)) is a new, isolated interface — text input, target language, voice selection (all via `SynthesizeAsync`'s parameters), synthesis result and timing metadata (`TtsSynthesisResult`, plus a per-chunk callback for streaming timing), and failure state (`Success`/`FailureReason`). It has no reference to `IAudioOutputSink` or any production audio type — confirmed by reading the file: no `using` of the `Audio` namespace, no production type referenced anywhere in `Streaming/`.

## 2. Azure TTS capability

**PROVEN, from this project's own established history, not re-verified live this step**: standalone Azure Speech TTS via `SpeechSynthesizer` with the existing `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` credentials has been used successfully, repeatedly, throughout this project's session — both bundled inside `AzureSpeechTranslationProvider`'s `TranslationRecognizer` (every `Synthesizing` event since Step 1) and standalone in `VTTranslate.LiveTest`'s own pre-existing audio-generation commands (`GenerateTestAudioAsync`, `GenerateMeasurementAudioAsync`, `GenerateStep4AudioAsync`, `GenerateStep5AudioAsync`), all of which successfully produced real synthesized WAV files earlier in this same session, before this step's WDAC block appeared. **This is not an assumption — it is the same credential and the same underlying Azure capability, already exercised dozens of times in this project.** `AzureStreamingTtsProvider` ([src/VTTranslate.Core/Streaming/AzureStreamingTtsProvider.cs](../../src/VTTranslate.Core/Streaming/AzureStreamingTtsProvider.cs)) wraps this exact same mechanism behind the new isolated interface — no new credential path, no new capability assumption. **What is genuinely new and UNVALIDATED THIS STEP**: a *fresh* live confirmation via this specific new class, and the specific chunk-size/latency/cancellation experiments this step was asked to run — none of that live data was obtained (see headline finding).

## 3-4. Test languages and latency measurement points

Both directions were coded into the live harness (`streaming-tts-feasibility-test` in `VTTranslate.LiveTest/Program.cs`): German synthesis ("Hallo.") and English synthesis ("Hello.") for the capability check, plus 4 chunk-size cases spanning both languages (§6). T0 (candidate-Speakable) → T1 (TTS request start) → T2 (first chunk) → T3 (synthesis complete) → T4 (handed to test sink) are all implemented and wired in `StreamingTtsPipelineExperiment.SubmitAsync` — **PROVEN by code review**, **UNVALIDATED THIS STEP by execution** (the WDAC block prevented the harness from running).

## 5. Streaming vs. non-streaming synthesis

**PROVEN, from documented Azure Speech SDK behavior already relied upon elsewhere in this exact project**: `SpeechSynthesizer`'s `Synthesizing` event fires once per audio chunk as it is generated, strictly before the final `SynthesisCompleted`/`SpeakTextAsync` result — this is the same, already-used mechanism `AzureSpeechTranslationProvider`'s own `Synthesizing` handler relies on for its T5 latency measurement (documented since Step 1's measurement work). `AzureStreamingTtsProvider` surfaces this exact mechanism via its `onChunk` callback — **not invented, not assumed; the same SDK behavior this project has depended on since early in its history.** Whether the FIRST chunk in practice arrives meaningfully before full-synthesis completion for short phrases (i.e., whether "streaming" provides a real latency win at this utterance scale) is **UNVALIDATED THIS STEP** — no live chunk timing data was obtained.

## 6. Chunk-size experiment

Coded, not executed: 4 cases (A very short German "Ja.", B short English conversational, C medium German sentence, D longer English sentence) were built into the live harness. **UNVALIDATED THIS STEP** — the WDAC block prevented any of these from running.

## 7. Revision safety

**PROVEN (by manual trace of the implementation and its unit tests — see §14)**: `StreamingTtsPipelineExperiment.SubmitAsync` checks `request.IsStable` as the FIRST condition, before any interaction with `IStreamingTtsProvider` — an unstable candidate never reaches TTS, structurally (traced: `UnstableCandidate_NeverCallsTts_NeverReachesSink` — `provider.Calls` is asserted empty). Once a stable candidate IS sent, generation is re-checked a SECOND time immediately after synthesis completes (not just before starting) — this specifically catches a revision (generation bump) that occurs WHILE synthesis is in flight, traced in `StaleGeneration_DiscardedAfterSynthesisCompletes_NeverReachesSink`: a synthesis that succeeds is still discarded, never reaching the test sink, if the generation moved on during the call. No audible correction/retraction is attempted anywhere in this code — a rejected/stale/unstable candidate simply produces nothing, preferring silence, exactly as instructed.

## 8. Consecutive chunks

**PROVEN (by manual trace)**: `ConsecutiveChunks_DeliveredInOrder_NoDuplicates` traces three sequential submissions (different sequence numbers, same utterance/generation) and confirms delivery order `[1, 2, 3]` with no duplication. Each request carries an explicit `(UtteranceId, Generation, SequenceNumber)` identity, and a `HashSet` of already-delivered keys (`_delivered`) rejects any repeat — traced in `DuplicateSubmission_RejectedOnSecondAttempt_NeverDoubleDelivered`. Stale chunks after a reset are rejected — traced in `Reset_RejectsSubsequentRequestsFromThePreResetGeneration` (a pre-reset-generation request submitted after `Reset()` is rejected as stale, never reaching the sink a second time).

## 9. Cancellation

**PROVEN (by manual trace)**: `Cancellation_BeforeFirstAudio_NeverReachesSink` traces a request cancelled during a delay before any chunk arrives — the underlying `OperationCanceledException` is caught by `SubmitAsync` and mapped to `Cancelled`, never reaching the sink. `StaleGeneration_RejectedBeforeSynthesis_WhenAlreadyStaleAtSubmission` traces a request whose generation is already stale at submission time — rejected before any TTS call is even attempted (`provider.Calls` empty). The live harness additionally codes a "new generation while old synthesis is pending" scenario (cancel 30ms after starting a longer sentence) — **UNVALIDATED THIS STEP by live execution**, though the same code path is covered by the unit-test traces above.

## 10. Failure handling

**PROVEN (by manual trace)**: HTTP/API failure (`SynthesisFailure_NeverReachesSink_ReasonReported`), empty synthesis (`EmptySynthesis_TreatedAsFailure_NeverReachesSink`), and cancellation are all handled without retry, without an unbounded queue (there is no queue at all — one request, one synthesis attempt, one outcome), and without stale audio reaching the sink after any of these outcomes. Rate limiting and malformed audio were not specifically simulated in unit tests this step (not reproducible safely without a live call, which was blocked) — **UNVALIDATED THIS STEP**.

## 11. Audio format

**ASSUMED-then-configured, not live-confirmed this step**: `AzureStreamingTtsProvider` explicitly sets `SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm` — 16kHz sample rate, 16-bit depth, mono, PCM encoding, RIFF/WAV container. This is not a guess: it is the exact same format constant this project's own `VTTranslate.LiveTest` audio-generation helpers already use (confirmed by reading `GenerateTestAudioAsync`'s existing code, which sets the identical `SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm`), chosen here for direct consistency and compatibility with every other WAV file this project already produces and consumes. Whether the isolated `InMemoryTestPlaybackSink` "accepts" this format is moot for this step — it never inspects or validates audio content, only counts bytes (by design, to guarantee it can never play audio) — so no format-compatibility risk exists for the test sink specifically. **Confirmation via a real synthesized byte stream is UNVALIDATED THIS STEP.**

## 12. Voice selection

A normal Azure neural voice was used for each direction in the (unexecuted) live harness: `de-DE-KatjaNeural` for German, `en-US-JennyNeural` for English — the same voices this project has used throughout its entire session. No voice cloning or voice preservation of any kind was implemented or considered.

## 13. Latency — honestly, none obtained this step

**UNVALIDATED THIS STEP.** No median, p95, or maximum latency figures can be reported, because no live synthesis call succeeded this step (WDAC blocked execution before any call could be made). This is stated plainly rather than substituting historical figures from unrelated bundled-TTS measurements (Step 1's T5 figures measure a materially different code path — TTS bundled with translation inside `TranslationRecognizer` — not this step's standalone `SpeechSynthesizer` call, and reusing those numbers here would misrepresent them as this step's own data).

## 14. Unit tests

**Written, and manually traced end-to-end for logical correctness (13 tests) — NOT machine-executed this step, due to the WDAC block (§ headline finding).** Every test's expected outcome was hand-verified against the actual implementation code, tracing exact state transitions (analogous to the manual-trace discipline this project has applied in every prior WDAC-blocked occurrence): `StableCandidate_SynthesizesAndDeliversToTestSink`, `UnstableCandidate_NeverCallsTts_NeverReachesSink`, `Cancellation_BeforeFirstAudio_NeverReachesSink`, `SynthesisFailure_NeverReachesSink_ReasonReported`, `EmptySynthesis_TreatedAsFailure_NeverReachesSink`, `StaleGeneration_DiscardedAfterSynthesisCompletes_NeverReachesSink`, `StaleGeneration_RejectedBeforeSynthesis_WhenAlreadyStaleAtSubmission`, `ConsecutiveChunks_DeliveredInOrder_NoDuplicates`, `DuplicateSubmission_RejectedOnSecondAttempt_NeverDoubleDelivered`, `Reset_RejectsSubsequentRequestsFromThePreResetGeneration`, `Reset_EvenAResubmissionOfAnAlreadyDeliveredKeyNoLongerMatters_StaleGenerationCaughtFirst`, `PlaybackSinkFailure_DoesNotPreventSubsequentSubmissions`, `NeverLogsSynthesizedText_OnlyMetadata`. **No test result is being claimed as "passed" — only as "manually traced and expected to pass based on code inspection," an important, deliberate distinction per this project's standing anti-fabrication rule.**

## 15. Security

**PROVEN (by code review)**: the single `_logger.Log` call site in `StreamingTtsPipelineExperiment.cs` (`LogOutcome`) includes only `utteranceId`, `generation`, `sequenceNumber`, `isStable`, and `outcome` — never synthesized text, never the source text, never any credential. `AzureStreamingTtsProvider`'s failure-reason strings are built from `ex.GetType().Name` only, never the exception message or the input text, avoiding any risk of leaking text through a failure path. No persistent storage of raw speech text was added.

## 16. Production isolation — explicit confirmations

- **`AzureSpeechTranslationProvider.cs` was not modified**: confirmed by grep — 0 matches for `StreamingTts` or `IStreamingTtsProvider` anywhere in the file.
- **No production audio path invokes the new TTS experiment**: confirmed — the only callers of `StreamingTtsPipelineExperiment`/`AzureStreamingTtsProvider` are the new unit test file and the new `streaming-tts-feasibility-test` harness command.
- **No Google Meet routing is involved**: confirmed — no reference to any Meet-routing or virtual-cable type anywhere in this step's new files.
- **No physical user audio was generated**: confirmed structurally — `InMemoryTestPlaybackSink` only appends metadata records to an in-memory list; it has no reference to `NAudio`, `WasapiOut`, or any audio-output device, and `AzureStreamingTtsProvider`'s `SpeechSynthesizer` is constructed with a `null` `AudioConfig` (no automatic device output), so even the raw Azure SDK call itself cannot play through a physical device unless explicitly configured to — confirmed by reading the constructor call (`new SpeechSynthesizer(config, null)`).

## 17. Documentation

This document.

## 18. Final decision — explicit answers, honestly qualified by this step's execution block

1. **Can Azure TTS produce the required target-language audio?** **Yes — but this is historical evidence from this project's own prior, successful use of the identical credential and mechanism (§2), not a fresh confirmation from this step's own execution**, which was blocked.
2. **Can first audio arrive early enough to be useful?** **UNVALIDATED THIS STEP.** No live first-chunk timing data was obtained.
3. **Is streaming synthesis actually supported?** **Yes, mechanistically** (the `Synthesizing` event fires per-chunk before completion — the same mechanism already relied upon elsewhere in this project, §5) — but whether it provides a *useful* latency win at real utterance scale is **UNVALIDATED THIS STEP**.
4. **What latency does TTS add?** **UNVALIDATED THIS STEP** — no number can be honestly reported.
5. **Can unstable/revised translation be prevented from reaching TTS?** **Yes — PROVEN by code design and manual test trace** (§7): the stability check runs first, before any TTS interaction, and a post-synthesis generation re-check catches revisions that occur mid-flight.
6. **Can stale synthesis be safely cancelled?** **Yes — PROVEN by code design and manual test trace** (§9): both a genuine `CancellationToken` cancellation and a post-completion generation mismatch are handled, and in neither case does output reach the test sink.
7. **Is the complete translation → TTS pipeline now ready for a controlled end-to-end experiment?** **No — not yet, and specifically not because of a design failure.** The architecture (isolated interfaces, stability/generation gating, no production coupling) is sound by code review and manual trace, but **this step could not obtain the live latency/behavior evidence it set out to gather**, due to an environmental WDAC block outside this session's control. Per instruction, "yes" is never answered merely because synthesis theoretically succeeds — and here, this step cannot even confirm synthesis succeeded fresh, so the honest answer is that a genuine live run of this exact experiment (or a retry once the WDAC condition clears) is a prerequisite before any further step, not a formality.

## Final Verification

- **Test count**: 13 new tests written; **0 confirmed executed** (WDAC blocked `dotnet test` for the entire session duration despite multiple full clean rebuilds and retries — same class of block documented repeatedly earlier in this project). All 13 were manually traced against the implementation and are expected to pass based on that trace (§14) — this is explicitly not the same as a passing test run, and is not claimed as one.
- **Build**: **0 warnings, 0 errors** — confirmed multiple times across full clean rebuilds.
- **Secret scan**: clean (§15).
- **Production files changed**: none — `AzureSpeechTranslationProvider.cs` confirmed untouched.
- **Experiment files added**: `StreamingTtsModels.cs`, `AzureStreamingTtsProvider.cs`, `StreamingTtsPipelineExperiment.cs` (new).
- **Live TTS calls**: **0 obtained this step** — WDAC blocked both `dotnet test` and the `VTTranslate.LiveTest` console executable (confirmed via `dotnet run`, `dotnet exec`, and repeated clean rebuilds); `VTTranslate.App.exe`, built from the identical `VTTranslate.Core.dll`, continued to load and run normally throughout, confirming the block is host/executable-specific, not a defect in this step's code. No bypass was attempted.
- **Latency statistics**: none — honestly reported as unavailable this step (§13), not fabricated or estimated.
- **Recommendation**: **retry this exact experiment (the `streaming-tts-feasibility-test` command and the full unit-test suite) once the WDAC condition clears**, before drawing any conclusion about real TTS latency or proceeding further. The architecture itself is ready and isolated; the evidence this step was designed to produce simply could not be collected this session. Do not treat the absence of a blocking design flaw as equivalent to a positive feasibility result — question 7 above is explicitly answered "No, not yet," for exactly this reason.

---

## Re-run addendum — attempted once, blocked again, now FROZEN

A single re-run of this experiment was attempted in a later session, per an explicit "evidence completion only" instruction: no redesign, no implementation change, first exhaust `dotnet test`, and only proceed to the live Azure TTS run if the unit-test suite executed.

**Commands attempted, in order:**
1. `dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj --no-build` → **blocked**: `System.IO.FileLoadException: Could not load file or assembly '...VTTranslate.Core.dll'. An Application Control policy has blocked this file. (0x800711C7)`.
2. Killed stale `testhost`/`dotnet`/`VTTranslate.App`/`VTTranslate.LiveTest` processes; deleted `bin`/`obj` for `VTTranslate.Core` and `VTTranslate.Core.Tests`; `dotnet build VTTranslate.sln` → succeeded, 0 warnings/0 errors.
3. Re-ran `dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj --no-build` → **blocked again**, identical `0x800711C7` error, now surfacing on a different test than attempt 1 — confirming the whole assembly load is blocked, not one specific test.

**Result of the re-run**: 297 of 299 tests reported failed with `FileLoadException`; 2 reported passed (almost certainly tests that never touch the blocked assembly, not evidence of partial success). Per instruction, the live Azure TTS capability run (T0-T4 timing, both directions, short/medium/long utterances, cancellation, stale-generation, consecutive utterances, revision A→B, empty/error/timeout behavior) was **not attempted**, since its prerequisite (an executing unit-test suite) was never reached. No test is claimed as passed. No latency figure, real or estimated, is reported for the re-run.

**Freeze decision, per explicit instruction following the re-run**: this experiment is now **FROZEN — UNVALIDATED**. No further WDAC recovery attempts are to be made, no further live-execution attempts are to be made, and no implementation change is to be made to `StreamingTtsModels.cs`, `AzureStreamingTtsProvider.cs`, or `StreamingTtsPipelineExperiment.cs` until validation occurs in a separately authorized execution environment. Step 5.13 has not been started. Everything in §1-18 above (the original Step 5.12 report) stands exactly as originally written — the re-run changed no code and produced no new data to incorporate, only this confirmation that the blocking condition persisted.
