# Changelog

All notable changes to Kivrio Agent UI are documented in this file.

## [2026.5.6] - 2026-05-06

### Added
- Added an OpenCode workspace settings panel under User > Settings.
- Added configurable Windows base folders and custom paths for OpenCode workspaces.
- Added server-side OpenCode workspace validation, creation, Windows-to-WSL path resolution, and protection against using the application folder as a workspace.

### Changed
- Routed OpenCode turns through the configured workspace instead of the Kivrio Agent UI application folder.
- Strengthened OpenCode system instructions so new projects are created only inside the configured OpenCode workspace.
- Kept Codex and model selection behavior unchanged while adding the OpenCode workspace pipeline.
- Anonymized workspace previews in the settings UI while keeping the real path internal.

### Removed
- Removed the obsolete static Claude Code option from the agent selector markup.

## [2026.5.5] - 2026-05-05

### Added
- Added a coding agent selector with stable per-conversation agent locking.
- Added OpenCode support through WSL while keeping the existing Codex CLI adapter intact.
- Added agent metadata to local conversations so each conversation keeps its selected coding agent.

### Changed
- Kept the existing model selector behavior unchanged while separating model selection from coding agent selection.
- Improved local agent status and diagnostics for the Codex and OpenCode workflow.
- Stabilized the WSL prompt handoff by using temporary prompt and script files instead of fragile inline shell quoting.

### Removed
- Removed Claude Code from the active selector because its non-interactive WSL authentication path was not reliable enough for release.

## [Unreleased] - 2026-05-01

### Changed
- Reframed the active documentation around Kivrio Agent UI instead of the old Kivrio release history.
- Kept the existing chat UX while restoring local session authentication in the autonomous C# server.
- Kept the current Ollama-facing frontend while the Codex CLI bridge is still under construction.

### Removed
- Removed obsolete active documentation references to old Kivrio release notes.
- Removed obsolete local runtimes and legacy math pipeline dependencies from the project tree in the previous cleanup phase.
