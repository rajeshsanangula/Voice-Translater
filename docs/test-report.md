# Windows MVP — Test Report

## Update — capture pump-pacing defect found, fixed, and regression-tested

A local (non-Meet) diagnostic isolating a reported Google Meet EN↔DE failure found and
fixed a real defect in `AudioCaptureSource.PumpLoop` — full detail directly below,
before the rest of this document (which predates the fix and is otherwise unchanged).

### The defect

`AudioCaptureSource`'s pump thread called `_resampler.Read(readBuffer, 0, 3200)` in a
loop, falling back to `Thread.Sleep(10)` only when `Read()` returned 0. The underlying
`BufferedWaveProvider` was constructed with its default `ReadFully = true`, which
**zero-pads every read to the full requested length** instead of honestly returning 0
when the real captured-audio queue is empty. That made `read > 0` true on essentially
every call, so the sleep/throttle branch was dead code — the pump free-ran at CPU
speed, emitting "100ms" chunks (by byte-count label) thousands of times faster than
real time, mostly silence padding. Measured live before the fix: **437,481 reads
(1.40 GB) captured in 5 seconds**, instead of the intended ~50 reads (~160 KB). This
hit every consumer of `AudioCaptureSource` — both `CaptureKind.Microphone` and
`CaptureKind.SystemLoopback` — but never the WAV-injection test path
(`TestFileAudioInputSource`), which paces itself independently. That's exactly why
every earlier `pipeline-test` run passed while the live capture path was broken: the
passing tests never exercised the buggy code.

**Direct evidence this caused real recognition failures, not just excess data**: a
local reproduction (physical Speakers → WASAPI loopback → real Azure DE→EN pipeline,
no Google Meet or browser involved) returned **zero transcript, zero translation**
before the fix, despite real, correctly-leveled German audio genuinely reaching the
capture buffer (independently confirmed via `cable-loopback-test`'s RMS measurement).
After the fix, the identical test produces a correct transcript and translation. This
is the closest thing to a smoking gun available without a live Meet call: it proves
the pacing defect alone was sufficient to break recognition end-to-end.

### The fix

Two parts, in [`AudioCaptureSource.cs`](../src/VTTranslate.Core/Audio/AudioCaptureSource.cs):
1. `_buffered.ReadFully = false` — makes `Read()` honestly report "nothing available"
   as 0 instead of padding.
2. The polling `Thread.Sleep(10)` fallback was replaced with a proper wait on a new
   [`RealTimeAudioPump`](../src/VTTranslate.Core/Audio/RealTimeAudioPump.cs) helper: an
   `AutoResetEvent`-based signal, set by the `WasapiCapture.DataAvailable` callback
   whenever real new data arrives, with the pump thread blocking on it (bounded by a
   50ms timeout, so shutdown stays responsive even with no more signals). This is a
   proper event-driven wait, not just a smaller poll interval — chosen deliberately
   over the "smallest possible fix" (just flipping `ReadFully`) because a bare
   `Thread.Sleep(N)` throttle is still a guess at the right pace; waiting on a real
   "data arrived" signal is correct regardless of the source's actual delivery cadence
   (relevant here since Bluetooth HFP delivers audio in irregular bursts, not a steady
   16kHz stream). `Stop()` also signals the pump immediately for prompt shutdown rather
   than waiting out the poll timeout.

Preserved unchanged: the 16kHz/16-bit/mono output contract, `IAudioInputSource`
interface, cancellation/shutdown ordering, and all Azure/translation/TTS/VB-CABLE
code — this was purely a capture-pacing fix.

### Regression tests added

[`RealTimeAudioPumpTests.cs`](../tests/VTTranslate.Core.Tests/RealTimeAudioPumpTests.cs)
(4 new tests, hardware-independent):
- `SimulatedPumpLoop_DoesNotSpin_WhenSourceIsAlwaysStarved` — drives the exact
  starved-source loop shape from `PumpLoop` for 200ms; asserts read-attempt count is
  in the 1–40 range (a correctly-paced loop), not the ~17,500 the pre-fix defect would
  produce in the same window.
- `WaitForData_WakesPromptly_WhenSignaled_RatherThanWaitingOutTheFullTimeout` — proves
  the signal path works, not just the timeout backstop.
- `WaitForData_ReturnsAfterPollTimeout_WhenNeverSignaled` — proves shutdown
  responsiveness even with zero signals.
- `SignalDataAvailable_BeforeWait_IsNotLost` — proves `Stop()`'s "wake immediately"
  call is correct even if the pump thread hasn't reached the wait yet.

### Build & full test suite (post-fix)

```
dotnet build VTTranslate.sln  →  Build succeeded. 0 Warning(s). 0 Error(s).
dotnet test  →  Passed! Failed: 0, Passed: 22, Skipped: 0, Total: 22
```
(18 pre-existing + 4 new `RealTimeAudioPump` tests.)

### Diagnostic re-run (post-fix) — the same four tests that exposed the defect

| Test | Before fix | After fix |
|---|---|---|
| 1 — Mic capture (`Headset (Noise Airwave Max 5)`, Bluetooth HFP) | 437,481 chunks / 1.40GB in 5s — defect | **72 chunks / 130,240 bytes in 5s** (81% of the 160,000-byte ideal — the gap is normal Bluetooth burst-delivery cadence, not a defect: chunk count is now in the expected tens, not hundreds of thousands) |
| 2 — CABLE Input → CABLE Output (independently measured) | 96,768,000 bytes captured for a ~4s phrase — defect, but real signal (RMS 109.3) still got through | **174,400 bytes** for the same phrase (correct real-time volume), RMS 2574.2 — PASS |
| 3 — English TTS → `Headphones (Noise Airwave Max 5)` (the actual output endpoint — `Headset` has no render endpoint at all) | Unaffected (uses `AudioPlaybackSink`, never buggy) — PASS | Unaffected — PASS |
| 4 — Speakers → WASAPI loopback → real DE→EN Azure pipeline | 185,216,000 bytes captured; **zero transcript, zero translation** — full reproduction of the reported failure | **209,280 bytes** (correct real-time volume); transcript "Ich möchte morgen ein treffen vereinbaren." → "I would like to arrange a meeting tomorrow." — **PASS** |

### Acceptance Tests 1–2, concurrency, cancellation, restart — re-run post-fix

All re-run live against real Azure after the fix, all consistent with pre-fix results
(this fix touched only the live-capture path, not the WAV-injection path these mostly
already exercised — re-run for completeness per instruction, not because a regression
was expected):
- **Acceptance Test 1 (EN→DE)**: PASS — "I would like to schedule a meeting tomorrow."
  → "Ich möchte für morgen einen Termin vereinbaren."
- **Acceptance Test 2 (DE→EN)**: PASS — "Ich möchte morgen ein treffen vereinbaren."
  → "I would like to arrange a meeting tomorrow."
- **Concurrent bidirectional**: PASS — no deadlock, no cross-contamination, no
  duplicate results.
- **Cancellation/shutdown**: PASS — 0 late events after full shutdown; clean restart
  afterward.
- **5-cycle restart loop**: PASS — all 5 cycles correct, working set stable
  (46.6→47.6MB), managed memory growth 12KB across 5 cycles (noise-level).

### Remaining blocker (unchanged) — Google Meet still requires a human test

This fix is proven **locally** — including a direct local reproduction and resolution
of a Meet-failure-shaped symptom (zero ASR result on a real captured stream). It has
**not** been re-tested through an actual Google Meet call, because that requires a
human physically present with a real second participant — exactly the same
requirement noted throughout this document. **Do not read this fix as "Google Meet is
now confirmed working"** — it removes the most likely root cause found so far, but the
real Meet test is still outstanding and is the correct next step.

---


Last updated: 2026-09-09, systematic re-validation pass (items 1–15). Read
[`docs/test-methodology.md`](test-methodology.md) first — it defines what each
verification tier actually proves.

## This pass — systematic validation checklist (items 1–15)

| # | Item | Result |
|---|---|---|
| 1 | Build: 0 errors, 0 warnings | **PASS** — all 4 projects |
| 2 | All unit/integration tests | **PASS** — 18/18 |
| 3 | Live Azure English → German | **PASS** — real Azure call, correct transcript+translation |
| 4 | Live Azure German → English | **PASS** — real Azure call, correct transcript+translation |
| 5 | Concurrent bidirectional | **PASS** — no deadlock, no cross-contamination, no duplicates |
| 6 | Cancellation / intentional shutdown | **PASS** — 0 late events after full shutdown, live Azure |
| 7 | Reconnect logic | **CODE-VERIFIED only** — live fault injection deliberately not attempted (would require system-wide network/firewall changes not authorized) |
| 8 | Repeated start/stop/restart (5 cycles) | **PASS** — 5/5 cycles correct, working set stable (44.1→45.7MB, no growth trend), managed memory growth 13KB across 5 cycles (noise-level) |
| 9 | No duplicate transcripts / stale callbacks | **PASS** — confirmed live (1 final result per utterance across concurrent + 5 restart cycles) + code-verified (generation guard) |
| 10 | Device enumeration/selection | **PASS** — 7 real devices enumerated correctly (3 input, 4 output) with correct IDs |
| 11 | Feedback-loop protection | **PASS** — unit-tested, and separately confirmed live in an earlier session when the user's manual UI test correctly triggered the block for a real conflicting device selection |
| 12 | WAV/test-audio injection path | **PASS** — exercised in every live test this session via `TestFileAudioInputSource` |
| 13 | Latency metrics | **DOCUMENTED** — see below, honest per-stage breakdown |
| 14 | UI/config/error-handling review | **PASS with 1 noted gap** — see below |
| 15 | This document | **Updated** |

### Item 14 detail — UI/config/error-handling review findings

Reviewed `MainViewModel.cs` and `MainWindow.xaml` end-to-end (no code changes):
- Validation (`SessionValidator`) runs *before* any session starts, blocking on missing
  devices/config or the feedback-loop conflict — confirmed correct control flow.
- Errors surface via `LastError` bound to a red `TextBlock`; reconnect/status text has
  its own separate binding (`ReconnectStatus`) so the two don't overwrite each other.
- `StartAsync`'s catch block calls `StopAsync()` on any partial-start failure, so a
  failure starting the second direction doesn't leave the first direction's session
  (and its live Azure connection) orphaned.
- **Noted gap (not fixed, out of scope for this pass)**: no UI handling for a selected
  device being unplugged/removed mid-session (e.g., unplugging a Bluetooth headset
  while running) — `AudioCaptureSource`/`AudioPlaybackSink` would raise an error through
  the existing error path, but this specific scenario hasn't been tested live.

## Verification status summary

| Layer | Status |
|---|---|
| **CODE VERIFIED** | ✅ Yes — build clean (0 errors/warnings), 18/18 unit tests pass |
| **AZURE API VERIFIED** | ✅ Yes — real calls to the configured Azure Speech resource (region `eastus`) succeeded: TTS generation, streaming ASR, translation, streaming synthesis all confirmed with real request/response data below |
| **AUDIO PIPELINE VERIFIED** (file-injection through real Azure, production classes) | ✅ Yes — Acceptance Tests 1, 2, and 3 all ran against live Azure using the actual production `AzureSpeechTranslationProvider`/`DirectionPipeline`/`LatencyBreakdown` classes |
| **PHYSICAL MICROPHONE/ACOUSTIC VERIFIED** | ⏳ Still PENDING — requires a human to run the app and speak; the agent has no microphone/ears (see test-methodology.md). Unchanged from prior report. |
| **MEETING ROUTING VERIFIED (Google Meet/Teams)** | ❌ NOT verified — unchanged from prior report. VB-CABLE installation and one-way OS routing were confirmed; an actual browser-microphone loopback was not, because the sandboxed Browser pane blocks mic access and no real Chrome is connected via Claude-in-Chrome. **This is explicitly NOT marked PASS**, per your instruction, even though the underlying OS mechanism works. |

---

## Environment

| Item | Value |
|---|---|
| OS | Windows 10 Home Single Language, build 10.0.26200.0 (version 25H2) |
| .NET SDK | 8.0.424 |
| Azure Speech resource | **Configured.** `AZURE_SPEECH_KEY: PRESENT` (value never displayed, logged, or written to any file — see "Secret handling" below). `AZURE_SPEECH_REGION: eastus`. |
| Credential loading | Confirmed working via `AppSettings.GetEnv`, which explicitly reads `EnvironmentVariableTarget.User` from the registry rather than relying on process environment inheritance — necessary because every shell/tool process in this session predates the variable being set, so `$env:AZURE_SPEECH_KEY` shows empty in a plain shell check even though the app correctly detects it. Confirmed both via a standalone detection check and via real successful Azure API calls. |
| Provider | `AzureSpeechTranslationProvider` (Azure AI Speech SDK, `Microsoft.CognitiveServices.Speech` 1.40.0) |
| VB-Audio Virtual Cable | Installed, signature-verified (unchanged from prior report) |

---

## CODE VERIFIED — build, tests, regressions found and fixed this pass

```
dotnet build VTTranslate.sln  →  Build succeeded. 0 Warning(s). 0 Error(s). (all 4 projects)
dotnet test  tests/VTTranslate.Core.Tests  →  Passed! Failed: 0, Passed: 18, Skipped: 0, Total: 18
```

### Regression found and fixed this pass

Re-running the full test suite after the Azure key became genuinely present at User
scope surfaced a real bug: two tests (`AppSettingsTests.IsProviderConfigured_False_WhenKeyOrRegionMissing`,
`SessionValidatorTests.Validate_ReturnsError_WhenProviderNotConfigured`) started
**failing**, because they simulated "not configured" by clearing a Process-scope
override with an empty string — but .NET's `SetEnvironmentVariable` treats
null-or-empty as "delete this override," which let `AppSettings.GetEnv`'s fallback
chain reach through to the now-real User-scope key. Fixed in `EnvVarTestHelper` by
using a single space (blank per `IsNullOrWhiteSpace`, but a real non-empty override)
instead of an empty string. This is exactly the kind of test-infrastructure bug that
only surfaces once real credentials exist, so finding it now (rather than the tests
silently passing for the wrong reason before) is a genuine, useful catch.

---

## Acceptance Test 1 — English → German (LIVE, real Azure)

**Input**: `"I would like to schedule a meeting tomorrow."` (synthesized to real audio via Azure TTS, `en-US-JennyNeural`, then pushed through the production ASR→translation→TTS pipeline)

| Metric | Value |
|---|---|
| Actual ASR transcript | `I would like to schedule a meeting tomorrow.` |
| Actual German translation | `Ich möchte für morgen einen Termin vereinbaren.` |
| TTS completed | ✅ Yes — real German audio produced (`test-results/output-audio/test1_en_to_de_output.wav`) |
| Recognition+Translation latency | 1443 ms |
| TTS (synthesis) latency | 1306 ms |
| **End-to-end latency** | **2749 ms** |
| Errors/warnings | None |
| **Result** | **PASS** — transcript exactly matches expected input; translation is a fluent, correct German rendering (Azure chose "einen Termin vereinbaren" — "arrange an appointment" — over a literal "Meeting schedulen," which is the more natural German phrasing, not a translation error) |

## Acceptance Test 2 — German → English (LIVE, real Azure)

**Input**: `"Ich möchte morgen ein Treffen vereinbaren."` (synthesized via Azure TTS, `de-DE-KatjaNeural`)

| Metric | Value |
|---|---|
| Actual ASR transcript | `Ich möchte morgen ein treffen vereinbaren.` (lowercase "treffen" — a minor capitalization variance from ASR, not a content error) |
| Actual English translation | `I would like to arrange a meeting tomorrow.` |
| TTS completed | ✅ Yes — real English audio produced (`test-results/output-audio/test2_de_to_en_output.wav`) |
| Recognition+Translation latency | 1971 ms |
| TTS (synthesis) latency | 462 ms |
| **End-to-end latency** | **2433 ms** |
| Errors/warnings | None |
| **Result** | **PASS** — transcript and translation both correct in substance |

### Additional live cases run (beyond the two required phrases, for broader signal)

| Case | Result | Note |
|---|---|---|
| Numbers ("twenty twenty six", "four point seven million dollars", "twelve percent") | Harness comparator says FAIL | **Not a translation defect** — Azure correctly normalized spoken numbers to digit form ("2026", "$4.7 million", "12%"), which is objectively correct ASR behavior (inverse text normalization), but the test harness's naive word-overlap comparator doesn't recognize "twenty" as matching "2026." This is a test-harness limitation, documented honestly rather than hidden. |
| German compound words (Geschwindigkeitsbegrenzung, Lebensversicherungsgesellschaft) | PASS | Transcribed and translated correctly |
| English technical terms (Kubernetes, API gateway, deployment pipeline) | PASS | Transcribed and translated correctly, including correct German compounding ("Kubernetes-Cluster", "API-Gateway", "Deployment-Pipeline") |

---

## Acceptance Test 3 — concurrent bidirectional operation (LIVE, real Azure)

Ran the EN→DE and DE→EN acceptance phrases **simultaneously** via `Task.WhenAll`, each
through its own independent `AzureSpeechTranslationProvider` + `TestFileAudioInputSource`
instance (mirroring exactly how `MainViewModel` runs both `DirectionPipeline`s at once).

| Check | Result |
|---|---|
| Deadlock/crash | **CONFIRMED NONE** — `Task.WhenAll` completed normally |
| Wall time for both directions concurrently | 9.7s |
| Process CPU time consumed | 0.2s (confirms the wait is I/O-bound on the network, not busy-spinning) |
| Managed memory delta | 1330 KB |
| EN→DE result | PASS — `"I would like to schedule a meeting tomorrow."` → `"Ich möchte für morgen einen Termin vereinbaren."` (1 final result) |
| DE→EN result | PASS — `"Ich möchte morgen ein treffen vereinbaren."` → `"I would like to arrange a meeting tomorrow."` (1 final result) |
| Cross-direction contamination | **CONFIRMED NONE** — EN→DE transcript contains no German-only content and vice versa |
| Duplicate transcripts | **CONFIRMED NONE** — each direction produced exactly 1 final result |
| Feedback loop | Not directly exercisable in this file-injection harness (no live audio device loop exists here) — this remains **design + unit-test verified** (`SessionValidator`), unchanged from prior report |
| **ACCEPTANCE TEST 3** | **PASS** |

---

## Reconnect behavior — CODE VERIFIED (live fault-injection not attempted, by design)

Per your explicit instruction ("if a particular failure cannot be safely reproduced,
mark it CODE VERIFIED rather than inventing a live result"): forcing a genuine Azure
disconnect mid-session would require either killing the network adapter or adding a
temporary Windows Firewall rule to block the Azure Speech endpoint. Both are
system-wide, state-changing actions with side effects beyond this app (would also
disrupt any other network activity on the machine), so this was deliberately **not**
attempted without your explicit sign-off. Reconnect logic is CODE VERIFIED (unchanged
from the prior report): bounded to 5 attempts, exponential backoff via a
cancellation-aware `Task.Delay`, all recognizer lifecycle transitions serialized
through a lock with a post-lock re-check of the stop flag, and a generation counter
preventing a superseded instance's callback from producing duplicate output.

## Cancellation/shutdown — LIVE-VERIFIED this pass (real Azure connection)

Built a dedicated live test: started a real session against Azure mid-utterance
(waited for the first real partial ASR result to confirm recognition had genuinely
begun), then called `StopAsync()`+`DisposeAsync()`.

| Check | Result |
|---|---|
| `StopAsync`+`DisposeAsync` completed | 1222 ms |
| Events during the stop-drain window | 3 — **expected, not a bug**: Azure's `StopContinuousRecognitionAsync` is documented to gracefully flush recognition already in flight before returning; these fired *during* that documented drain, before shutdown had fully completed |
| Events observed **after** shutdown fully completed (the real invariant that matters) | **0** — confirmed clean over a 5-second observation window afterward |
| A brand-new session started immediately after | **PASS** — `"I would like to schedule a meeting tomorrow."` → `"Ich möchte für morgen einen Termin vereinbaren."`, proving the app can start a clean new session right after a shutdown with no leaked state |
| **CANCELLATION/SHUTDOWN TEST** | **PASS** |

This test genuinely moved several of the previously "code-verified only" items to
live-verified: items 5 (clean shutdown), 6-partial (no reconnect after Stop — none was
triggered here since this was an intentional stop, not a failure, but the shutdown
path exercised is the same one the reconnect-cancellation logic depends on), 9 (no
duplicate transcripts post-shutdown).

---

## Latency — honest breakdown and what it does/doesn't measure

Using the real production `LatencyBreakdown` class (unmodified) against live Azure:

| Stage | What it measures | Test 1 (EN→DE) | Test 2 (DE→EN) |
|---|---|---|---|
| Capture/input | *Not separately instrumented.* `TestFileAudioInputSource` paces WAV chunks in real-time to simulate live capture cadence, but no timestamp is recorded at "audio chunk captured" — only from first partial recognition onward. | n/a | n/a |
| Recognition + Translation | From first partial ASR result to the final transcript+translation being available. Azure's `TranslationRecognizer` bundles ASR and MT into one event, so these two stages are **not separable** with this provider architecture — a real, disclosed limitation, not an oversight. | 1443 ms | 1971 ms |
| TTS (Synthesis) | From final-result timestamp to first synthesized audio chunk received. | 1306 ms | 462 ms |
| **End-to-end** | From first partial result to synthesized audio ready — i.e., pipeline-internal latency. | **2749 ms** (this pass: 1578 ms) | **2433 ms** (this pass: 2449 ms) |

Re-measured this pass (single run): Test 1 Rec+MT 1138ms / TTS 440ms / E2E 1578ms; Test 2
Rec+MT 2083ms / TTS 366ms / E2E 2449ms. The EN→DE number moved noticeably between
passes (2749ms → 1578ms) — this reflects real Azure network/service variance run to
run, not a code change; no latency-affecting code changed between the two
measurements. Across the 5-cycle restart-loop test, total per-cycle wall time
(including WAV playback pacing, not just recognition) was consistently 8.9–9.6
seconds, showing no upward drift over repeated cycles.

**Important distinction — this is NOT the same as true user-perceived latency.**
True user-perceived latency would additionally include: (a) the time from when a
person actually starts speaking to when the audio subsystem delivers the first
capture chunk (not measured — would require hardware-timestamped audio callbacks),
and (b) the time from synthesized audio being *ready* to it actually being *audible*
through the output device's buffer (WASAPI buffering, typically tens of ms, not
separately measured here). The numbers above measure the pipeline's own processing
latency using file-injected audio at real-time pace, which is a reasonable proxy but
not identical to a stopwatch-in-hand human test.

---

## Secret handling — final scan, this pass

- `AZURE_SPEECH_KEY: PRESENT`, `AZURE_SPEECH_REGION: eastus` — reported per your
  instruction, value never displayed.
- Grepped all `.cs` files for 32+ character tokens: 2 false positives, both long
  method/test names (`CreateAndStartRecognizerLockedAsync`, etc.) — no actual secret.
- `%APPDATA%\VTTranslate\settings.json` inspected directly: contains only device-ID
  fields (`MicrophoneDeviceId`, etc.), no key/region field exists in it at all —
  confirms `[JsonIgnore]` on `AzureSpeechKey`/`AzureSpeechRegion` is working as
  designed.
- `test-results/*.json` and `*.md` (including this run's live pipeline/concurrent/
  cancel-test reports) scanned for 32+ character tokens: none found.
- The WPF app's UI displays only a 4-character masked tail of the key (e.g.
  `****XXXX`) — confirmed rendering correctly in a screenshot taken this pass, which
  was then **deleted immediately** since even a 4-character masked fragment is more
  than "never write the key to screenshots" should tolerate; the actual masked
  characters are not reproduced anywhere in this report either.
- `.gitignore` excludes `test-results/`, downloaded installer binaries, and common
  secret-file patterns (unchanged from prior report).
- **No git commits exist in this repository** (`git log` → "does not have any commits
  yet") — nothing has ever been pushed or committed, so there is no history to leak
  from.

---

## Meeting routing (Google Meet/Teams) — unchanged from prior report, correctly NOT marked PASS

No new attempt was made this pass (explicitly out of scope — no driver installation
this session). Recapping the prior finding, since this section must not be marked PASS
without a real demonstration: VB-CABLE (single cable) is installed and
signature-verified; the production `AudioPlaybackSink` was confirmed able to write
real audio into it; but a browser actually picking that up as a microphone was
**not** demonstrated. **MEETING ROUTING: NOT VERIFIED.**

**Root cause identified and confirmed empirically this week**: the single installed
VB-CABLE has only one capture endpoint (`CABLE Output`) shared by both of its playback
endpoints (`CABLE Input` and `CABLE In 16ch` — confirmed via PnP topology, both share
parent `ROOT\MEDIA\0000`, and empirically via VB-Audio's own Control Panel meter
showing a signal on the single "Cable Input" section when audio was played to either
endpoint). This means one cable cannot serve as both the German-output device and the
Meet-loopback-source device — using the same cable for both is exactly what the
feedback-loop guard correctly refuses. A second independent cable (VB-CABLE A+B) is
required to route Meet's outbound/inbound legs separately; that product is
paid/donationware (~$15 via VB-Audio's webshop, not a free direct download) and has
**not** been purchased or installed, per explicit instruction not to purchase/install
anything this session. See the routing plan proposed earlier in this project's history
for exact device assignments once/if that's approved.

---

## Known limitations (unchanged unless noted)

- Direction is fixed per pipeline — no automatic EN/DE language auto-detection.
- No barge-in/interruption handling.
- Bluetooth device classification is name-heuristic (documented, misclassifies this
  session's Bluetooth headset).
- No kernel-mode virtual audio driver bundled; VB-CABLE is an external dependency.
- Recognition+Translation latency is not separable into ASR-only and MT-only numbers
  with the current single-connection Azure `TranslationRecognizer` architecture.
- Capture-to-first-audio-chunk latency and output-buffer-to-audible latency are not
  separately instrumented (see "Latency" section above).
- No mobile, cellular, voice cloning, or backend/billing work — out of scope.

## Remaining MVP work

1. A human-run acoustic test (real microphone, real headphones) — the only way to
   close the "physical microphone/acoustic" tier.
2. A human-run Google Meet/Teams test (steps documented in the prior report version,
   unchanged) to attempt "MEETING ROUTING VERIFIED."
3. A live reconnect test, if you explicitly authorize a network-interruption method
   (e.g., a temporary firewall rule) — not attempted without that sign-off.
4. Optional: improve the test harness's transcript-comparison heuristic to recognize
   correct inverse-text-normalization (spoken numbers → digits) as a match, so the
   "numbers" test case reports PASS instead of a misleading FAIL.
