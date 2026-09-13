# MVP Plan

## Goal

A Windows desktop app that:
1. Captures English microphone speech, translates it to German, and plays/routes the
   German speech out (to speakers or a selected output device, e.g., a virtual
   microphone the user has installed).
2. Captures German speech arriving from a selected input (microphone, or system
   loopback from a meeting app), translates it to English, and plays it to the
   user's headphones/speakers.
3. Shows live status: connection, current languages, measured latency, device
   selection, start/stop.
4. Has a live optional transcript view (source + translated text).
5. Fails gracefully and visibly when the network, provider, or an audio device is
   unavailable.

## Explicit non-goals for this MVP

- No virtual audio driver is shipped — the user picks an already-installed one
  (e.g., VB-Audio Virtual Cable) from a device dropdown if they want meeting-app
  routing.
- No voice cloning — standard Azure neural voices only.
- No mobile apps.
- No accounts, billing, or cloud backend — fully local desktop app, API key stored
  in local config.
- No automatic language auto-detection in v1 pass — the user explicitly sets
  direction (English mic → German out; German in → English out) since Azure's
  speech translation recognizer requires a fixed source language per connection.
  (Noted as a fast-follow, not faked.)

## Required from the user before this can run live

An Azure AI Speech resource key + region (Azure Portal → "Speech" resource → Keys
and Endpoint). Without it, the app builds and the UI works, but Start Translation
will show a clear "provider not configured" error rather than pretending to work.

## Definition of done for MVP

- `dotnet build` succeeds with zero errors.
- `dotnet test` passes for all unit tests (device enumeration logic, session state
  machine, config validation — not tests requiring a live Azure key).
- App launches, lets you pick input/output devices, lets you enter/save an Azure key
  in Settings, and Start Translation begins a live session when a key is present.
- Manual test script in `docs/MVP.md` documents the exact repeatable demo steps.
- Known limitations are written down, not hidden.
