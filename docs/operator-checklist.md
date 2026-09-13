# Operator Checklist — Human-Assisted Windows MVP Validation

For the live Google Meet + Bluetooth headset validation that cannot be run by the agent
(requires a human speaking, listening, and a real remote participant). No code changes
are needed to run this — it exercises the already-implemented, already-unit-tested
(70/70) build.

---

## 0. Before you start — distinguishing evidence types

As you go, keep these three categories separate in your own notes, and in whatever you
report back:

- **Observed live behavior** — you personally saw/heard it happen during this session.
- **Automated/unit-test evidence** — already proven by the 70/70 test suite (e.g. "mute
  produces zero PushAudio calls" is unit-proven, not something you need to re-derive
  live — you're confirming the UI/hardware path around it, not re-proving the logic).
- **Blocked** — could not be attempted (missing hardware, no participant, etc.).

Never upgrade a "blocked" or "unit-tested" item to "observed" in your notes.

---

## 1. Reconnect the Bluetooth headset

- Power on **Noise Airwave Max 5** and confirm it's paired/connected in Windows Bluetooth settings.
- Confirm both of its Windows audio endpoints are present and `Active`:
  - `Headset (Noise Airwave Max 5)` — capture (microphone) device
  - `Headphones (Noise Airwave Max 5)` — render (playback) device
- If either is missing: unpair/re-pair, or check Windows Bluetooth troubleshooter, before continuing.

## 2. Verify Windows recording/playback devices

Open Windows Settings → Sound, and confirm all of the following show as **Active**:

| Device | Direction | Should show as |
|---|---|---|
| `Headset (Noise Airwave Max 5)` | Recording | Active |
| `Headphones (Noise Airwave Max 5)` | Playback | Active |
| `Speakers (3- Cirrus Logic XU)` | Playback | Active |
| `CABLE Input (VB-Audio Virtual Cable)` | Playback | Active |
| `CABLE Output (VB-Audio Virtual Cable)` | Recording | Active |

## 3. Verify VTTranslate device selections

Launch the app, and in **Audio Devices** confirm exactly:

| Field | Value |
|---|---|
| Microphone | `Headset (Noise Airwave Max 5)` |
| Remote audio input (loopback source) | `Speakers (3- Cirrus Logic XU)` |
| English output | `Headphones (Noise Airwave Max 5)` |
| German output | `CABLE Input (VB-Audio Virtual Cable)` |

Confirm the **Azure Speech Provider** panel shows "Loaded from environment" with a
region and masked key — not "NOT CONFIGURED".

## 4. Verify Google Meet microphone/speaker selections

In the Meet call's device settings (or Chrome/Edge site settings for meet.google.com):

| Meet setting | Value |
|---|---|
| Microphone | `CABLE Output (VB-Audio Virtual Cable)` |
| Speaker | `Speakers (3- Cirrus Logic XU)` |

---

## 5–6. Test phrases (use exactly these, verbatim)

**EN→DE test phrase** (you speak this into the real microphone):
> "I would like to schedule a meeting tomorrow."

**DE→EN test phrase** (ask your remote participant to speak this):
> "Ich möchte morgen ein Treffen vereinbaren."

---

## 7. PASS/FAIL criteria per direction

**EN→DE — PASS requires ALL of:**
- Live Transcript shows an `[EN→DE]` line with recognized English text reasonably matching the phrase (minor punctuation/casing differences are fine; a completely different or garbled sentence is not).
- German translation text shown alongside it is a plausible German rendering of the phrase.
- The remote participant (ask them directly) confirms they heard German audio through Meet, at a time consistent with when you spoke.
- No `LastError` (red text) appears.

**EN→DE — FAIL if:** transcript is empty/garbled, no German audio reaches the participant, or an error appears.

**DE→EN — PASS requires ALL of:**
- Live Transcript shows a `[DE→EN]` line with recognized German text reasonably matching the phrase.
- English translation text shown alongside it is a plausible English rendering.
- You personally hear English audio through your headphones, at a time consistent with when the participant spoke.
- No `LastError` (red text) appears.

**DE→EN — FAIL if:** transcript is empty/garbled, you hear nothing (or hear raw German instead of English), or an error appears.

---

## 8. Bidirectional alternating-conversation test

1. Speak the EN→DE phrase.
2. Wait for it to fully resolve (transcript line appears, participant confirms they heard German).
3. Have the participant immediately speak the DE→EN phrase.
4. Wait for it to fully resolve.
5. Repeat steps 1–4 two more times (3 full round-trips total), alternating who speaks first on the last round.

**PASS requires:**
- Exactly one `[EN→DE]` transcript line per English utterance, one `[DE→EN]` line per German utterance — no duplicates, no line appearing twice.
- Each transcript line's language/direction matches who actually spoke (no cross-contamination — an English utterance never produces a `[DE→EN]` line or vice versa).
- No feedback loop (see §10) at any point.
- No crash, no `LastError`, `Status` stays `Running` throughout.

## 9. Mute test

**9a. Idle mute (already unit-tested; this confirms the live UI/hardware path):**
1. While running and not speaking, click **Mute Microphone** — confirm button label flips to **Unmute Microphone**.
2. Speak the EN→DE phrase while muted.
3. **PASS**: no `[EN→DE]` transcript line appears, no German audio reaches the participant.
4. Click **Unmute Microphone** — confirm label flips back.
5. Speak the EN→DE phrase again.
6. **PASS**: transcript line appears normally, participant hears German.

**9b. Mid-utterance mute:**
1. Start speaking the EN→DE phrase.
2. Partway through (e.g., after "I would like to"), click **Mute Microphone**.
3. Finish the sentence (your voice, but the app is now muted).
4. **PASS**: no German audio reaches the participant for this utterance (the Live Transcript MAY still show a partial/final line — that's expected and correct per the implemented design; only the audio must be suppressed, not the transcript).
5. **FAIL**: participant hears any German audio for this utterance.
6. Unmute, then speak the EN→DE phrase cleanly one more time — **PASS**: this next utterance is NOT suppressed (confirms no leak into the following utterance).

## 10. Feedback-loop test

While bidirectional conversation is active (§8), watch for:
- German TTS output being picked up by the loopback (`Speakers`) and re-recognized/re-translated as if it were new German speech (would show up as an unexpected extra `[DE→EN]` transcript line right after a `[EN→DE]` line, with no participant having spoken).
- Any transcript line repeating/looping without new speech.

**PASS:** no such lines ever appear. **FAIL:** any unexplained extra transcript line appears without a corresponding real utterance from either side.

---

## 11. Diagnostic log evidence to collect

Log location: `%AppData%\VTTranslate\logs\diagnostic-<date>.log`

**Before the call:**
- Confirm the `logs` folder either doesn't exist yet or note the current latest file's last line/timestamp, so you can identify what's new after the call.
- **Do not delete this folder while the app is running** (doing so mid-session silently breaks logging for that session — confirmed the hard way in a prior validation pass).

**After the call, collect and save a copy of the log file showing:**
- `Connected` lines for both `en-US->de-DE` and `de-DE->en-US` with their generation numbers.
- `AudioChunksSent` lines — confirm they pause (no new lines, or a visible gap) during the mute window in §9a/9b, and resume after unmuting.
- Any `UtteranceEvaluated` lines — note `accepted=true/false` and `reason` for each (this is metadata only, never recognized text).
- Any `AudioSuppressed` lines during the mute tests (§9) — these should correlate with the muted windows.
- Any `Disconnected` / `ReconnectAttempt` / `ReconnectResult` lines, if a reconnect happens to occur naturally during the call (do not force one).
- `Shutdown` lines at the end, confirming `StopAsync called` and `DisposeAsync completed` for both directions with no errors after.

---

## 12. Clean start / clean stop procedure

**Clean start:**
1. Confirm no previous `VTTranslate.App` process is already running (check Task Manager) — if one is, close it fully first.
2. Launch the app fresh.
3. Complete steps 2–4 above (device verification) before clicking Start.
4. Click **Start Translation**. Confirm `Status` becomes `Running` and no `LastError` appears before proceeding to any test.

**Clean stop (after all tests):**
1. Click **Stop**.
2. Confirm `Status` becomes `Stopped`, app remains responsive (not frozen/crashed).
3. Close the app normally (window close button), not via Task Manager kill, so shutdown logging completes.
4. Only then collect the log file per §11.

---

## 13. Safety note

Nothing in this checklist requires disabling Windows security features, changing
firewall/network settings, or otherwise destabilizing the environment. Reconnect
behavior (Test 6 from the prior validation round) is intentionally **not** included
here — it remains verified only at the unit-test level, since there's no safe way to
force a live Azure/network fault without deliberately breaking something.
