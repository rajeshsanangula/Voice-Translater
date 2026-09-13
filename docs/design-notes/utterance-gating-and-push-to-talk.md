# Utterance Gating (Layer 1) and the Push-to-Talk Gap (Future Layer 2)

## What Layer 1 (`UtteranceEligibilityGate`) actually is

An **utterance-quality gate**, not speaker identification. It decides whether a
recognized+translated utterance is reliable enough (based on Azure's own segment
duration and, when available, confidence score) to forward its synthesized audio to
the far end of a call. It filters:

- Noise blips too short to be a real word (< 250ms).
- Low-confidence misrecognitions, when Azure supplies a confidence score.
- Short, atypically-dense/sparse word patterns when no confidence score is available.

It deliberately never inspects audio amplitude/RMS — a quiet, clearly-spoken utterance
is treated identically to a loud one.

## What Layer 1 does NOT solve

**Genuine third-party speech that Azure recognizes clearly and confidently.** The
diagnostic session that led to this gate captured a real example: a fluent,
grammatically coherent ~9-word English sentence, picked up by the microphone while the
intended user was silent, over roughly 2.5 seconds. That utterance is *not* short, and
if it were genuinely clear audio, Azure's own confidence score for it could plausibly
be high — because from Azure's point of view, it correctly transcribed real speech. No
duration or confidence signal can distinguish "the intended user's own voice" from
"someone else's voice picked up by the same sensitive microphone" — that is a speaker
identification / proximity problem, not a quality problem, and this gate does not
attempt it.

## Planned future mitigation: user-controlled mute / push-to-talk

The only fully deterministic fix for third-party speech reaching the far end of a call
is a control the user directly operates: a mute toggle or push-to-talk mode that
prevents the microphone pipeline from producing translated output at all unless the
user has explicitly signaled "I am speaking now." This is a **product/UX decision**
(where does the control live, what's the default state, does it apply per-session or
persist, keyboard shortcut vs. UI button, foot-pedal support for accessibility, etc.)
that deserves its own design pass — it is not implemented as part of this gate, and
should not be treated as "coming for free" alongside it.

### Why it's not bundled into this change

It's a UI/UX-facing control (touches `MainWindow.xaml`/`MainViewModel` state and
requires a decision on interaction model), not a pure backend quality filter — bundling
it here would mix two different kinds of change and two different kinds of review
(a backend correctness fix vs. a user-facing interaction design). It's flagged here so
the gap is visible and doesn't get forgotten, not because it's difficult to build.

## Update — Layer 2 implemented: Toggle Mute (EN→DE only)

**Implemented** (not yet Push-to-Talk — that's a follow-up using the same primitive):
explicit, user-controlled mute for the microphone (EN→DE) direction, via a "Mute
Microphone" button. Default state is **Unmuted** — fully backward-compatible.

### Where it lives

`DirectionPipeline.SetMuted(bool)` is the sole primitive. `AudioCaptureSource` is never
started/stopped by muting (avoids unnecessary Bluetooth profile-switch risk — see the
earlier Bluetooth diagnostic session). `AzureSpeechTranslationProvider` has zero
awareness mute exists — reconnect, generation, and cancellation logic are completely
unmodified and untouched by this feature.

### Two independent suppression points, for two different situations

1. **Steady-state mute**: captured audio is simply never forwarded to
   `provider.PushAudio` while muted. If mute was already active before an utterance
   started, that utterance never begins recognition at all — nothing to suppress
   later.
2. **Mid-utterance mute**: if mute is toggled on while an utterance is already in
   flight, its audio must still be prevented from reaching playback once Azure
   finishes recognizing/synthesizing it. This uses a **deterministic, non-timing-based
   correlation**: a per-`DirectionPipeline` utterance sequence number, incremented
   exactly once per `FinalResult` (Azure's own declared utterance boundary). If mute
   was active at any point during that utterance, its sequence number is recorded as
   suppressed; every subsequent `AudioSynthesized` event is checked against that exact
   value (not "consume once") — so all of a suppressed utterance's streamed audio
   chunks are dropped, and the check stops applying the instant the next utterance's
   `FinalResult` advances the counter again. No sleep, no timer, no risk of leaking
   into the next utterance.

Transcript display is unaffected in both cases — mute only ever gates audio output.

### Testing

`IAudioOutputSink` was extracted from `AudioPlaybackSink` (minimal interface
extraction, no behavior change) specifically so `DirectionPipeline` could be
unit-tested directly with fakes for capture/provider/playback — 18 new tests in
`DirectionPipelineMuteTests.cs` cover default state, forwarding gating, mid-utterance
suppression (including multi-chunk TTS and non-leakage into the next utterance),
transcript visibility, reconnect/cancellation independence from mute, two independent
pipeline instances, and a rapid-toggle stress test — all fully synchronous/
deterministic, no timing assumptions anywhere in the test suite either.

### Still not implemented

Push-to-Talk (hold-to-speak) UI wiring, and any control for the DE→EN direction — both
explicitly deferred per the approved plan. Neither this nor Layer 1 is speaker
identification, voice biometrics, or voice cloning — none of that is implemented or
planned in this phase.
