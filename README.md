# StayVibin

A native Windows (WPF / .NET 10) GUI front-end for OpenHands - a local-first
AI vibe-coding assistant.

It does **not** reimplement the agent. Instead it launches the existing OpenHands
`agent-server` (a local FastAPI backend) as a child process and talks to it over
REST + WebSocket - the same architecture the official OpenHands web app uses.
This keeps all of OpenHands' model integrations, tools, and streaming while giving
you a real native desktop app.

This project is standalone and intentionally separate from the HCDE build and
release pipeline. It is built on the upstream MIT-licensed OpenHands.

## How it works

```
+-------------------+        REST: POST /api/conversations        +------------------+
|  StayVibin        |  ----------------------------------------->  |  agent-server    |
|  (C#/WPF)         |        WebSocket: /sockets/events/{id}       |  (Python,        |
|                   |  <-----------------------------------------  |   localhost:8000)|
+-------------------+        live events / messages               +------------------+
                                                                      |
                                                                      v
                                                                 Ollama / LLM
```

- `Services/BackendManager.cs` - locates and launches `agent-server.exe`, waits
  for the `/health` endpoint, and tears the process down on exit.
- `Services/AgentSpecProvider.cs` - reuses the agent config the OpenHands CLI
  saved in `~/.openhands/agent_settings.json` and forces non-native tool calling
  (so local models do not leak tool-call syntax).
- `Services/AgentServerClient.cs` - creates a conversation over REST and streams
  events over the WebSocket, normalizing them into render-ready updates.
- `MainWindow.xaml` / `.cs` - the chat UI: messages, thoughts, tool calls,
  results, status, and a server log panel.

## Prerequisites

1. **.NET 10 SDK** (to build) - the runtime is bundled when you publish.
2. **OpenHands agent-server** - StayVibin installs this for you automatically the
   first time you press **Start** (it installs `uv` if needed, then runs
   `uv tool install openhands`). To do it manually instead:
   ```
   uv tool install openhands
   ```
   The app expects `agent-server.exe` at
   `%APPDATA%\uv\tools\openhands\Scripts\agent-server.exe` (override in Settings).
3. **A configured model** - on first launch StayVibin shows a provider setup box
   (Ollama for now) and writes `~/.openhands/agent_settings.json` for you. If a
   config already exists (e.g. from the OpenHands CLI) it is reused as-is.
4. **Ollama** running with at least one chat model pulled (e.g. `ollama pull
   qwen2.5-coder:14b`). More local providers are planned.

> Note: the agent-server, sdk, and tools packages must be version-compatible.
> This repo was validated against `openhands-sdk==1.21.0` /
> `openhands-tools==1.21.0` with `openhands-agent-server==1.21.0`.

## Run (development)

```
dotnet run
```

## Build a distributable .exe

```
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Produces a self-contained single-file executable at:

```
bin\Release\net10.0-windows\win-x64\publish\StayVibin.exe
```

## Build the Windows installer (v1.0+)

```
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

This publishes a self-contained `StayVibin.exe` and compiles a full setup wizard using
Inno Setup 6 (downloaded automatically if it is not already installed). Output:

```
dist\StayVibin-<version>-setup.exe
```

The installer adds Start menu and Add/Remove Programs entries. Optional desktop shortcut.
It does **not** bundle OpenHands or Ollama - install those separately (see Prerequisites).

Download the latest release from
[GitHub Releases](https://github.com/bokoxthexchocobo/StayVibin/releases).

## Using it

1. Press **Folder** to choose the working directory the agent operates in.
2. Press **Start** - the app launches the backend, waits for health, creates a
   conversation, and connects.
3. Type a task and press **Enter** (Shift+Enter for a newline). **Stop**
   interrupts a running agent.

## Current scope (v1.0)

- Single conversation per session, with Start/Stop, Interrupt, and Steer.
- Renders user/assistant messages, agent thoughts, tool calls, results, errors.
- Model dropdown sourced from local Ollama, with hot-swap mid-session and a
  refresh button to pick up newly pulled models instantly.
- Auto-tunes temperature / reasoning / context per selected model (toggleable).
- Token + context usage strip with manual and automatic compaction.
- Model capability strip (chat / tools / vision / etc.) with tooltips.
- Attachments: the `+` button, drag-and-drop, and screenshot paste; images are
  shown to vision-capable models, videos are frame-sampled when ffmpeg is present.
- Settings window for connection, model, and behavior; settings/logs live under
  `%APPDATA%\StayVibin`.
- Live token streaming is rendered if the backend emits it; otherwise the final
  message is shown when the turn completes.

## What's new in v1.0.2

- Context cap now supports "auto": leave Settings -> "Max context" blank and
  AutoTune uses each model's native window; with AutoTune off it defaults to 32k.
  Enter a number to force a specific size.
- The context meter updates immediately when you change the cap in Settings (no
  restart needed) and always reflects the configured runtime window.
- Server log expander sits flush with the window bottom when collapsed - the empty
  gap under it is gone; expanding restores a resizable, draggable log panel.

## What's new in v1.0.1

- Model dropdown refresh button - newly pulled Ollama models appear without a restart.
- Context window now adapts per model up to a user-set ceiling (Settings -> "Max
  context"); raise it for more context, lower it to save RAM. Default 64K.
- Step-by-step narration of agent actions (Run command, Read/Edit/Create file,
  Plan, ...) instead of a generic tool line.
- Leaked tool-call markup (`<function=...>`) is stripped from assistant messages.
- Reliability: model metadata no longer caches transient failures, model loading
  is serialized, session teardown no longer blocks the UI, and external command
  helpers read stdout/stderr concurrently to avoid pipe-buffer deadlocks.

## License

StayVibin is released under the MIT License (Modified with Ethical AI Use Clause) -
see [LICENSE](LICENSE). In short: standard MIT permissions, plus an ethical-use
restriction that prohibits using the Software to cause intentional harm (e.g.
disinformation, non-consensual deepfakes, phishing, autonomous weapons, malicious
cyber-attacks or surveillance, or any illegal activity). Breaching that clause
automatically voids the license.

This project is built on the upstream MIT-licensed OpenHands, which retains its
own license.
