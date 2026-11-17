# Windups development gateway

**Current main test mode (2026-11-06):** `WindupsMeeting.directOpenAI=true` bypasses this gateway. Editor reads the existing ignored key locally; Node does not need to run. Embedding the key in a test APK needs a local opt-in (`LocalSettings/direct-build-consent.txt`). A key inside an APK can be extracted, so never publish such an APK. The setup below remains available when `directOpenAI=false`.

Node 22+ (tested with Node 24). This is a local prototype gateway, not a hosted production service.

```powershell
cd Server
npm ci
# If .env is missing in a fresh checkout: Copy-Item .env.example .env
# Open .env locally and paste the key after OPENAI_API_KEY= (no spaces needed).
npm start
```

`Server/.env` is intentionally excluded by `Server/.gitignore`; `.env.example` contains placeholders only and is safe to commit. Do not paste the key into chat, a Unity Inspector field, `gatewayToken`, screenshots, logs, or source code. The server only reports whether OpenAI is configured and never prints the key. Keep `HOST=127.0.0.1` for Editor testing.

The OpenAI API integration is already implemented: Unity streams PCM16 audio to this gateway, the gateway authenticates to OpenAI and sends only transcription events back to Unity. `OPENAI_API_KEY` is also used by the meeting-response endpoint. `WINDUPS_GATEWAY_TOKEN` is a separate restricted token for protecting a non-loopback gateway; it must never contain the OpenAI key.

Unity `WindupsMeeting.serverUrl` defaults to `http://127.0.0.1:8787`. Choose **Use OpenAI** in Play mode after starting the gateway. Without a key the gateway returns an explicit error; it never substitutes a scripted response.

For a headset in gateway mode, localhost is the headset, not the PC. Use a deliberate USB port reverse or a reachable HTTPS/WSS gateway. Non-loopback binding requires `WINDUPS_GATEWAY_TOKEN` (at least 24 characters); set the same restricted token on the meeting component. Do not expose this prototype directly to the public internet. USB forwarding was used for earlier development checks and is unnecessary for the direct test mode.

For USB development with the default loopback address, start the PC gateway, then run `metavr device list` and `metavr adb reverse tcp:8787 tcp:8787` with the intended Quest connected. Reapply the mapping if the USB debugging session resets. The APK uses the forwarded endpoint and the API key stays on the PC.

## Contracts

- `GET /health`: readiness and conservative process budget reservations.
- `POST /v1/turns`: `requestId`, `sessionId`, `trigger` (`user`, `reaction`, `wrapup`), `input`, `clarificationCount`, last 12 presented `history` lines. Returns `clarification` and `utterances` with server-issued IDs.
- `WS /v1/speech`: gateway sends `ready`; client streams binary PCM16 little endian, mono 24 kHz; text `commit` ends the utterance. Gateway returns JSON `delta`, `final`, or `error` messages. Maximum recording 60 seconds; one concurrent speech session. Unity stops at 59 seconds.
- OpenAI Responses structured JSON: default `gpt-5.4-mini`, configurable via `OPENAI_MODEL`. One request per batch, at most 6 initial opinions / 2 reactions, 2300 maximum output tokens. No web/image/3D/TTS tools yet.
- OpenAI realtime transcription: `gpt-live-transcribe`, manual commit, no VAD. Credentials stay in the gateway.

## Cost and failure behavior

No autonomous model loops. Unity pauses after two automatic speakers until user input/Continue; reading/selecting an opinion uses its existing text. A process-wide conservative $1 reservation ceiling reserves $0.04 per turn or recording. This is a development guard, **not invoice accounting**. Failures retain their reservation; no automatic retry. Restarting the server deliberately resets this in-memory ceiling. Changing model/prices requires revisiting the reservation. Configure account spending controls separately if needed.

Duplicate completed request IDs return the cached response (last 100); concurrent turns are rejected. This is a single-user prototype. No durable sessions, authentication accounts, multi-user queues or workspace database yet.

## Verification

`npm test` uses no OpenAI key and makes no paid API calls. It checks request bounds, reply links, duplicate roles, clarification limits, budget reservation and actual HTTP/WebSocket missing-key/authentication paths.

Actual model quality, realtime transcription, physical microphone capture and Quest networking remain unverified until a key and device are available.

References: [Responses structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs), [Realtime transcription](https://developers.openai.com/api/docs/guides/realtime-transcription).
