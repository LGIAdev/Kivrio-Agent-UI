## What's Changed

- Isolated Kivrio Agent UI on its own local port range (`8010-8019`) to avoid conflicts with Kivrio and Kivrio Chat.
- Added an application identity to `/api/health` so the launcher can verify that it is talking to Kivrio Agent UI before opening the browser.
- Reworked the Windows launcher to avoid slow HTTP port scans and keep startup responsive.
- Updated the visible README version to `Kivrio Agent UI 2026.5.7.1`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Agent-UI/compare/v2026.5.7...v2026.5.7.1
