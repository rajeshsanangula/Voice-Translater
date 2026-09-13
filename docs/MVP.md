# MVP Demonstration Script

## Prerequisites

1. An Azure AI Speech resource (Azure Portal → Create resource → "Speech"). Copy its
   **Key** and **Region**.
2. (Optional, for meeting-app routing) A virtual audio cable installed, e.g.
   [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) — free, installs a
   "CABLE Input"/"CABLE Output" device pair.

## Steps

1. `dotnet run --project src/VTTranslate.App/VTTranslate.App.csproj`
2. In **Azure Speech Provider**, paste your subscription key and region, click
   **Save Settings**.
3. In **Audio Devices**, select:
   - **Microphone** — your real mic (English speech in).
   - **Remote audio input (loopback source)** — the render device carrying the
     remote German participant's audio (e.g. your headphones/speakers device, if
     that's where a meeting app is playing German audio to).
   - **English output** — your headphones/speakers (where you'll hear English).
   - **German output** — either your speakers (for a live test with two people in
     the same room) or, for real meeting-app routing, the virtual cable's input
     device (e.g. "CABLE Input").
4. Click **Start Translation**. Status should move Idle → Starting → Running.
5. Speak English into the microphone. Watch the Live Transcript pane show
   `[EN→DE] ... → ...` lines, and listen for German speech on the German output
   device.
6. Play German audio toward the selected remote-input device (e.g. have someone
   speak near it, or play a German audio clip through it). Watch
   `[DE→EN] ... → ...` transcript lines and listen for English on your headphones.
7. Note the **Latency** readout (P90, milliseconds) — this is measured, not
   estimated.
8. Click **Stop**. Status moves to Stopped and both audio pipelines shut down
   cleanly.

## What to check for regressions

- No exception dialog on Start/Stop.
- If the German output device is set equal to the remote-input device, Start must
  refuse with the feedback-loop error rather than starting (see
  `SessionValidator.Validate`, covered by unit tests).
- If no Azure key is set, Start must show a configuration error, not silently do
  nothing or crash.

## Known limitations at this stage

- Direction is fixed per pipeline (no automatic EN/DE language detection yet).
- No barge-in/interruption handling yet — a full translated sentence plays out
  before the next segment starts.
- No virtual-microphone driver ships with the app; meeting-app routing depends on a
  separately installed virtual audio cable.
- Latency has not been benchmarked against a target number — the UI reports what it
  measures, with no claimed target.
