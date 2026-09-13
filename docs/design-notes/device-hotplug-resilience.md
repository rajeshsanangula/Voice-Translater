# Design Note — Windows Audio Device Hot-Plug Resilience

## What was audited

`AudioDeviceCatalog` (device enumeration), `AudioCaptureSource` (WASAPI capture/
loopback), `AudioPlaybackSink` (WASAPI render), and the four independent device slots
in `MainViewModel`/`AppSettings` (Microphone, Remote loopback source, English output,
German output).

## Finding 1: independent-endpoint support already existed

Each of the four slots is a fully independent device picker over the complete
enumerated device list — nothing in the architecture assumes "microphone and
headphones are the same physical device." This was already true before this phase and
is exercised in practice: a Bluetooth headset shows as two *separate* WASAPI
endpoints (`Headset (...)` for capture, `Headphones (...)` for render), and the app
happily lets you mix, e.g., a Bluetooth mic with built-in speakers, or a USB mic with
a virtual cable output. No code change was needed for this — it's an architectural
property that already held. Confirmed real device configurations working this
session: Bluetooth mic + Bluetooth headphones + virtual cable (German output) +
physical speakers (loopback source).

## Finding 2: device-kind classification is cosmetic-only (and imperfect)

`AudioDeviceCatalog.ClassifyDevice` uses a friendly-name heuristic (contains
"bluetooth", "usb", "cable", etc.) to label devices as BuiltIn/USB/Bluetooth/Virtual
for the UI. This is known to sometimes misclassify — e.g. a Bluetooth headset whose
friendly name doesn't contain "Bluetooth" gets labeled `BuiltIn`. **This label is
never used for functional decisions anywhere in the app** — `CaptureKind`
(Microphone vs. SystemLoopback) is chosen explicitly per pipeline slot at
configuration time, never inferred from the classification. A wrong label is a
cosmetic UI annoyance, not a correctness bug. Getting a reliable bus-type
classification would require walking the PnP device tree (SetupAPI/WMI, not just the
WASAPI endpoint's own properties) — out of scope for this pass; left as a known,
low-severity limitation.

## Finding 3: playback errors had no channel at all (real gap, fixed)

`IAudioOutputSink` had zero error-reporting surface before this phase. If the output
device disappeared mid-session (Bluetooth/USB disconnect), nothing detected or
reported it. **Fixed**: added `PlaybackError` to the interface, wired to
`WasapiOut.PlaybackStopped` in `AudioPlaybackSink` (an unexpected stop — i.e. one with
a non-null `Exception`, as opposed to our own `Stop()` call — now raises the event).
`DirectionPipeline` treats it exactly like the pre-existing `CaptureError`: a fatal,
clearly-labeled error.

## Design decision: fail-safe, not auto-reconnect

On a fatal capture or playback error, the app:
1. Surfaces a clear, direction-labeled error message.
2. `MainViewModel` now automatically calls `StopAsync()` for the **whole session**
   (both directions) — added this phase. Previously the session was left in an
   ambiguous "Status: Error but still nominally running" state requiring a manual
   Stop click.
3. Does **not** attempt to silently reconnect to the same device ID.

This is a deliberate choice, not an oversight. A device ID that disappears may:
- Reappear as a genuinely different device (Windows can assign new endpoint IDs on
  reconnect for some Bluetooth stacks).
- Never reappear (permanently removed hardware).
- Reappear correctly, but the "right" moment to resume isn't obviously knowable from
  inside the app.

Blindly reconnecting to a stale device ID risks silently routing audio to the wrong
place, or hanging indefinitely waiting for a device that's gone. The safe, honest
behavior is: stop cleanly, tell the user exactly what happened, let them reconnect the
hardware and click **Refresh Devices** (already existing) then **Start** again. This
mirrors the same philosophy already applied to Azure reconnection (bounded, explicit,
never silent) — just applied to physical hardware instead of a network socket, where
the failure modes are meaningfully different and less safe to auto-retry.

## "Reappearance" handling

No new code needed: `Refresh Devices` (pre-existing) re-enumerates; if the previously
selected device ID is available again, the (pre-existing, now-enhanced) validator
confirms it; the user clicks Start. No automatic detection-and-resume is implemented —
this is intentional, per the fail-safe design above.

## Windows default-device changes

Not applicable. The app never uses "the current default device" — every slot is an
explicit, user-chosen device ID persisted in settings. Windows changing its system
default has zero effect on this app's behavior.

## What was NOT validated

No physical hot-plug event (actually unplugging a USB mic, actually disconnecting
Bluetooth mid-session) was performed with real hardware during this phase — the
`PlaybackError`/`CaptureError` wiring is confirmed via unit tests with fake devices
that simulate the error event, not via an actual disconnect. Real hardware hot-plug
validation remains a remaining requirement (see the phase report).
