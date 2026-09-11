# GoMonitor

GoMonitor is a Windows system-tray utility that monitors [OpenCode Go](https://opencode.ai) API usage, with a built-in local reverse proxy that fixes the 400 error caused by Trae IDE not sending the required `x-opencode-session` header when calling OpenCode Go directly.

![GoMonitor icon](design/icon-bolt-trio.svg)

## Features

### Usage Monitoring

- Polls the official usage endpoint `GET https://opencode.ai/zen/go/v1/usage` every 60 seconds (configurable)
- Tray icon shows the usage level of the **5-hour / weekly / monthly** windows in real time
- Hover tooltip shows a compact one-line summary: `5h 13.0% | Week 12.0% | Month 8.0% | Off-Peak`
- Left-click opens a borderless dark usage panel: percentages, progress bars, reset countdowns, Refresh, Open Console
- On errors (missing key, network failure, parse failure) the last known data is kept and indicated with a gray icon plus an error summary

### Tray Icon

The icon is a glossy blue gradient rounded square with three lightning bolts — left to right: **5-hour / weekly / monthly**. Each bolt's color represents the usage level of its window:

| Color | Meaning |
| --- | --- |
| 🔵 Blue `#3FC6F5` | usage `< 60%` |
| 🟡 Yellow `#FFDD2E` | usage `60% – 85%` |
| 🔴 Red `#F5483F` | usage `≥ 85%` |
| ⚪ Gray `#8B949E` | error / no key / unavailable |

The icon is drawn at runtime with `System.Drawing` (see `src/GoMonitor/Services/TrayIconController.cs`). Design assets and previews live in the [design/](design/) directory (including a full-state preview page: [preview.html](design/preview.html)).

### OpenCode Local Reverse Proxy

Since 2026-09-06, OpenCode requires requests to `opencode.ai/zen/go/v1/*` to carry the `x-opencode-session` header; Trae IDE does not send it, resulting in 400 errors. GoMonitor's built-in proxy fixes this transparently:

- **Transparent forwarding**: listens on `127.0.0.1:9355` in-process and injects `x-opencode-session` / `x-opencode-request` / `x-opencode-client` / `x-opencode-project` / `User-Agent` headers automatically
- **Session management**: identifies sessions via a stable `SHA256(system prompt + first user message)` hash so the same conversation keeps the same session ID, preserving prompt-cache hits (~99%); supports idle expiry (6 hours by default) and a manual "New Session" reset
- **Model name rewriting**: model IDs with the `proxy-` prefix (e.g. `proxy-glm-5.3-flash`) are stripped before forwarding, preventing Trae from routing official model names through its own cloud channel
- **Token statistics**: parses usage from responses out-of-band (input / output / cache tokens), persists daily records to `proxy-usage/YYYY-MM-DD.jsonl`, and shows today's summary, per-model breakdown and recent requests in the panel
- **Model update hints**: periodically fetches the official model list and diffs it against the local snapshot, surfacing newly added / removed models in the panel
- **SSE streaming pass-through**: responses are not buffered as a whole; upstream requests are cancelled when the client disconnects

### Misc

- Start with Windows (writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, launched with `--minimized` so only the tray icon appears)
- Single-instance; closing windows only hides them — exit via the tray menu

## Getting Started

### Requirements

- Windows 10/11
- .NET 9 SDK (to build/develop); the published binary is self-contained

### Build & Test

```powershell
git clone https://github.com/ShawnHanXiao/gomonitor.git
cd gomonitor
dotnet build GoMonitor.sln
dotnet test GoMonitor.sln
```

### Publish a Single-File Executable

```powershell
dotnet publish src/GoMonitor/GoMonitor.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

The output is `publish/GoMonitor.exe` (~74 MB self-contained single file, runs without .NET installed).

## Authentication & Configuration

GoMonitor resolves the API key in this order:

1. The override key entered in the Settings window
2. The `opencode-go.key` entry in the local OpenCode auth file `~/.local/share/opencode/auth.json`

All data files live under `%APPDATA%\GoMonitor\`:

| File | Purpose |
| --- | --- |
| `settings.json` | App settings (poll interval, start with Windows, proxy on/off / port / UA, session idle hours, etc.) |
| `known-models.json` | Snapshot of the official model list (used for update diffs) |
| `proxy-usage/YYYY-MM-DD.jsonl` | Daily proxy request records (90-day retention by default) |
| `proxy-errors.log` | Proxy failure details, including inner exceptions |

## Using the OpenCode Proxy

1. Tray right-click menu → `Settings`, enable the proxy (or toggle it via the `Proxy: Off/Running` menu item)
2. Configure your OpenCode custom model in Trae as:

   - **Base URL**: `http://127.0.0.1:9355/zen/go/v1`
   - **Model ID**: `proxy-<real-model-name>`, e.g. `proxy-glm-5.3-flash`
   - **API Key**: may be left empty (the proxy fills in the local `auth.json` key)

3. Chat as usual; the panel shows today's token statistics and cache hit rate
4. Use the tray menu `New Session` to force a fresh session when needed

> The proxy only listens on loopback `127.0.0.1` and is never exposed to the LAN. Logs and statistics never contain prompt content or API keys in plaintext.

## Project Structure

```text
gomonitor/
  docs/                     # design documents (requirements, architecture, proxy design)
  design/                   # tray icon design assets and preview page
  src/GoMonitor/
    Models/                 # usage, settings and proxy data models
    Services/               # usage polling, tray drawing, proxy, sessions, stats, model catalog
    Views/                  # usage panel and settings window
  tests/GoMonitor.Tests/    # xUnit unit tests (parsing, thresholds, sessions, proxy end-to-end, etc.)
```

## Development

```powershell
dotnet build GoMonitor.sln   # build
dotnet test GoMonitor.sln    # run all unit tests (xUnit)
```

Key modules:

| Module | Responsibility |
| --- | --- |
| `OpenCodeUsageService` | Polls the official usage endpoint, outputs `UsageSnapshot` |
| `TrayIconController` | Runtime tray icon drawing, tooltip, tray menu |
| `OpenCodeProxyService` | HttpListener proxy: header injection, model rewriting, streaming, TLS retry |
| `SessionRegistry` | Session hash → ID mapping, idle expiry, manual reset |
| `ProxyUsageRecorder` | usage parsing (JSON / SSE), jsonl persistence and aggregation |
| `ModelCatalogService` | Fetches the official model list and diffs added / removed models |

## Documentation

- [00-Requirements & Design](docs/00-需求与设计方案.md)
- [01-Tech Stack & Architecture](docs/01-技术栈与架构.md)
- [02-OpenCode Proxy Design](docs/02-opencode-proxy设计方案.md)

## Acknowledgements

- [opencode-go-proxy-for-trae](https://github.com/LIMTCYT/opencode-go-proxy-for-trae) — reference for the header-injection approach
- [Trae forum: OpenCode session header discussion](https://forum.trae.cn/t/topic/180164) — verified that header injection preserves prompt caching
