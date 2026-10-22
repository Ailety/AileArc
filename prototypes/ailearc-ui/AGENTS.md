# Prototype Instructions

## AileArc design decision — 2026-10-21

- Preserve option 1's folder tree / file list / inspector layout.
- Simplify the top area using option 2: one archive heading and one navigation/action row, no repeated back/up controls.
- Show row checkboxes, header select-all and multi-selection details.
- User selected warm graphite as the default visual direction. Keep champagne accents restrained and refine the paired light theme. Warm gold is archived reference material, not a current theme choice.
- Browser preview remains an isolated mock. On 2026-10-22 the user accepted this interaction baseline and authorized the next stage. A separate .NET/WebView2 desktop validation host may reuse this UI through the strict `desktop.js` bridge for real read-only browsing. Mock extraction/creation must never be presented as real operations in desktop mode. Final UI framework selection remains provisional.

Run the local server yourself and open the preview in the browser available to this environment. Do not give the user server-start instructions when you can run it.

Before making substantial visual changes, use the Product Design plugin's `get-context` skill when the visual source is unclear or no longer matches the current goal. When the user gives durable prototype-specific design feedback, preferences, or decisions, record them in `AGENTS.md`.

When implementing from a selected generated mock, treat that image as the source of truth for layout, component anatomy, density, spacing, color, typography, visible content, and hierarchy.

Build app UI in `src/`. Keep `.openai/hosting.json`, `worker/index.js`, `scripts/prepare-sites-build.mjs`, and `tests/sites-worker.test.mjs` intact so the same local prototype can be handed to Sites. Before a Sites handoff, run `npm run build` and `npm run test:sites`; the build must leave `dist/client/index.html`, `dist/server/index.js`, and `dist/.openai/hosting.json`.
