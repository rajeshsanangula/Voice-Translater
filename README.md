# VT Translate

Real-time bidirectional German ⇄ English voice translation. **Windows desktop MVP.**

See [`docs/architecture.md`](docs/architecture.md) for the full design and what's
deliberately deferred (mobile, cellular calls, voice cloning, backend), and
[`docs/mvp-plan.md`](docs/mvp-plan.md) / [`docs/MVP.md`](docs/MVP.md) for scope and
the manual demo script.

## What actually works right now

- Real Windows audio device enumeration and capture (microphone + system loopback)
  via WASAPI/NAudio — no fake device lists.
- Real streaming speech-to-speech translation via the Azure AI Speech SDK
  (`TranslationRecognizer`) — ASR, translation, and neural TTS in one connection.
- Two independent directions running concurrently: English mic → German output, and
  German remote/loopback input → English output.
- A feedback-loop guard that refuses to start if the German output device is the
  same device being loopback-captured.
- Live status, measured P90 latency, live transcript, device selection, persisted
  settings.

## What does NOT work yet (see architecture doc for why)

- No virtual audio driver is bundled — you must install one yourself (e.g. VB-Audio
  Virtual Cable) if you want to feed German audio into Teams/Zoom/Meet.
- No automatic language detection — direction is fixed per pipeline, not
  auto-switched.
- No voice cloning, no mobile apps, no cloud backend/accounts/billing.

## Build

```bash
dotnet build VTTranslate.sln
```

## Test

```bash
dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj
```

## Run

```bash
dotnet run --project src/VTTranslate.App/VTTranslate.App.csproj
```

On first run, open the app, paste your Azure Speech resource key and region into
the "Azure Speech Provider" panel, pick your four audio devices, and click **Start
Translation**. Without a key, the app runs and the UI works, but Start will show a
clear configuration error instead of pretending to work.
