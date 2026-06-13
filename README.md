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
2. **OpenHands installed via uv**:
   ```
   uv tool install openhands
   ```
   The app expects `agent-server.exe` at
   `%APPDATA%\uv\tools\openhands\Scripts\agent-server.exe`.
3. **A configured model** - run the OpenHands CLI once so that
   `~/.openhands/agent_settings.json` exists (this app reuses it).
4. **Ollama** (or whatever provider the saved config points at) running.

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

## Using it

1. Press **Folder** to choose the working directory the agent operates in.
2. Press **Start** - the app launches the backend, waits for health, creates a
   conversation, and connects.
3. Type a task and press **Enter** (Shift+Enter for a newline). **Stop**
   interrupts a running agent.

## Current scope (v0.1)

- Single conversation per session, with Start/Stop, Interrupt, and Steer.
- Renders user/assistant messages, agent thoughts, tool calls, results, errors.
- Model dropdown sourced from local Ollama, with hot-swap mid-session.
- Auto-tunes temperature / reasoning / context per selected model (toggleable).
- Token + context usage strip with manual and automatic compaction.
- Model capability strip (chat / tools / vision / etc.) with tooltips.
- Attachments: the `+` button, drag-and-drop, and screenshot paste; images are
  shown to vision-capable models, videos are frame-sampled when ffmpeg is present.
- Settings window for connection, model, and behavior; settings/logs live under
  `%APPDATA%\StayVibin`.
- Live token streaming is rendered if the backend emits it; otherwise the final
  message is shown when the turn completes.
