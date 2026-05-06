# Kivrio Agent UI

![Status](https://img.shields.io/badge/status-WIP-blue)
![License](https://img.shields.io/badge/license-Apache--2.0%20%2F%20MPL--2.0-green)

Kivrio Agent UI is a local interface for using Codex CLI more comfortably with local models via [Ollama](https://ollama.com/).
It provides a desktop-style web UI with math rendering, local conversation history, and a fully local persistence layer.

Status: project under active development.
Version: Kivrio Agent UI 2026.5.6.

---

## Project status

Kivrio Agent UI is currently being rebuilt as a separate local interface.
Standalone release notes will be added when the first dedicated Agent UI package is produced.

---

## Current features

- Local Ollama integration
- Codex CLI bridge in local OSS mode through Ollama
- Coding agent selector for Codex and OpenCode
- OpenCode bridge through WSL
- Dark/light theme support
- Markdown rendering with KaTeX
- Conversation history in the left sidebar
- Persistent local storage of conversations in a JSON file
- Rename and delete actions for conversation links
- Local autonomous backend serving both the UI and the API
- Local session authentication
- Direct file reading for supported multimodal models

---

## Local architecture

Kivrio Agent UI now runs as a local application made of:

- a local autonomous Windows server
- a local JSON conversation store
- a browser UI served from the same local server
- Codex CLI and OpenCode as local coding agents
- Ollama running as the local inference server
- local Ollama models running outside Kivrio Agent UI, with `gpt-oss:20b` as the default model
- direct file reading for supported multimodal models

Conversation data is stored locally in:

`data/kivrio-agent-ui.json`

No cloud database is used for conversation history.

---

## Quickstart

### Windows

Run:

```powershell
.\start-kivrio-agent-ui.bat
```

Then open:

[http://127.0.0.1:8000/index.html](http://127.0.0.1:8000/index.html)

### Manual start

```powershell
cd "$env:USERPROFILE\Documents\Kivrio Agent UI"
.\bin\kivrio-agent-ui-server.exe --root . --host 127.0.0.1 --port 8000
```

Then open:

[http://127.0.0.1:8000/index.html](http://127.0.0.1:8000/index.html)

Make sure Ollama is installed locally and running, for example on:

`http://127.0.0.1:11434`

Kivrio Agent UI launches Codex through the Ollama integration. The target command shape is:

```powershell
ollama launch codex --model <local-ollama-model> -- app-server --listen ws://127.0.0.1:<port>
```

The default model is:

```text
gpt-oss:20b
```

For image files, Kivrio Agent UI keeps file upload support for compatible multimodal models.

### Authentication

Kivrio Agent UI currently runs as a local-only interface on `127.0.0.1`.
The autonomous backend protects local API routes with session-based authentication.

On first launch, the interface can create a local password stored in:

`data/auth.json`

Advanced local configuration keeps the Kivrio-compatible environment variables:

- `KIVRO_ADMIN_PASSWORD`
- `KIVRO_DISABLE_AUTH`
- `KIVRO_SESSION_TTL_SECONDS`
- `KIVRO_COOKIE_SECURE`

---

## Conversation history

Kivrio Agent UI stores conversations locally in a JSON store and rebuilds the left sidebar from that file at startup.

Supported behavior:

- reopen a saved conversation from the sidebar
- keep conversations after closing the interface
- keep conversations after a PC restart
- rename a conversation link
- delete a conversation link

Logging out of the interface no longer clears persistent conversation history.

---

## Project structure

- `index.html`: main UI
- `js/`: frontend logic
- `server/`: local API server source
- `css/`: styles
- `bin/kivrio-agent-ui-server.exe`: compiled local server, generated on demand or during packaging
- `data/kivrio-agent-ui.json`: local conversation store

---

## Roadmap

- [x] Basic UI with Ollama integration
- [x] Markdown + KaTeX rendering
- [x] Local conversation history
- [x] Local JSON persistence
- [x] Sidebar rename/delete actions
- [x] File uploads for supported multimodal models
- [x] Local session authentication
- [x] Codex CLI local bridge through Ollama
- [ ] Voice input/output

---

## Contributing

Contributions are welcome.

Recommended flow:

1. Fork the project
2. Create a branch
3. Open a Pull Request

See also `CONTRIBUTING.md`.

---

## License

The source code is distributed under a dual license: Apache 2.0 / MPL 2.0.

See `LICENSE`.

---

## Trademark notice

Kivrio Agent UI is derived from the Kivrio interface. References to Kivrio are kept for attribution, compatibility, and migration context.

The name Kivrio, its logo, and its visual identity are trademarks of LG-IA ResearcherLab.

For trademark inquiries: `contact@lg-ia-researchlab.fr`
