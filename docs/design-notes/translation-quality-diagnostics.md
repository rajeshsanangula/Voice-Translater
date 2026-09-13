# Design Note — Translation-Quality Diagnostics (ASR / MT / TTS / Routing)

## The constraint

`AzureSpeechTranslationProvider` never logs recognized/translated text (only its
length). That's a firm privacy rule from earlier in this project, unchanged here. So
"diagnosing a translation-quality problem" cannot mean "compare the logged text to
what was expected" — it has to mean "use metadata to narrow down which STAGE a
problem happened in," leaving the human who was actually on the call to supply the
content-level judgment ("that translation was wrong" / "it dropped a word" / etc.).

## What each existing/new log category tells you

| Symptom a human reports | Where to look | What it tells you |
|---|---|---|
| "Nothing happened at all" | `AudioChunksSent` (from `PushAudio`) | If **absent** entirely for the relevant window: audio never reached Azure — an **audio routing** problem (wrong device selected, mic muted, mic disconnected), not an ASR/MT/TTS problem. If **present**: audio did reach Azure. |
| "It seemed to mishear me / recognized nonsense" | `UtteranceEvaluated` (duration, confidence, accepted/reason) | A rejected or low-confidence entry around the right timestamp suggests an **ASR** problem (Azure itself was unsure) rather than translation or TTS — those stages never ran for a rejected utterance. |
| "It understood me but said nothing" | `RecognitionEvent kind=final` present, but **no** `AudioChunksSent`-adjacent `Synthesizing` activity, or a new `TtsMissing` entry | `TtsMissing` (added this phase) fires specifically when an *accepted* utterance produced no synthesized audio within 5 seconds — a **TTS**-stage-specific signal, distinguishing it from an ASR/MT failure (which would show as a low-confidence/rejected `UtteranceEvaluated` instead). |
| "It spoke, but not what I said" (a genuine translation-quality complaint) | Not distinguishable from logs alone | Azure's `TranslationRecognizer` bundles ASR+MT into one server-side result — there is no log signal that separates "ASR got the words right but MT translated them wrong" from "ASR misheard and MT correctly translated the wrong words." This is a hard SDK-imposed limit, not something logging can work around without either (a) a different provider architecture with separate ASR/MT calls, or (b) storing actual text for the specific session being investigated (a deliberate, temporary, opt-in exception to the privacy rule — not implemented). |
| "Audio came out garbled / choppy" | `AudioChunksSent` cadence + `AudioSuppressed` (mute) + the new `PlaybackError` (Part 3) | Distinguishes "our own mute/gate suppressed it" (`AudioSuppressed`, expected) from "the playback device itself failed" (`PlaybackError`, a hardware/routing issue) from "TTS produced audio but at an odd cadence" (would need latency data — see Part 4). |

## What's genuinely new this phase

- **`TtsMissing`**: a `System.Threading.Timer`-based watchdog armed whenever
  `UtteranceEligibilityGate` accepts an utterance; disarmed the instant ANY
  `Synthesizing` activity arrives for that generation (regardless of whether mute
  later suppresses it — the watchdog is about "did TTS produce anything," not about
  downstream forwarding). If 5 seconds pass with no synthesis activity, one log line
  fires. Generation-guarded (a stale watchdog from a superseded connection can't fire
  after a reconnect) and disarmed on `StopAsync`.

## What this does NOT do

- Does not, and cannot, tell you whether a specific translation was *linguistically*
  correct — that always requires either a human who understood both languages on the
  call, or (not implemented) an opt-in text-logging mode for active investigation of
  a specific reported problem.
- Does not separate ASR accuracy from MT accuracy — architecturally impossible with
  the current bundled-provider design without a larger rework.
