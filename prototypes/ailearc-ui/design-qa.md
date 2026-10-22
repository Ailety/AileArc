# AileArc prototype QA — 2026-10-21

Scope note (2026-10-22): the pass below records the accepted browser prototype iteration. The new read-only desktop host has a separate report in `docs/Desktop_UI_Validation.md`. Browser reloading of an existing internal error page was blocked by browser URL policy this turn; no new browser QA pass is claimed for that attempted reload.

final result: passed

## Current refinement — warm graphite selected

- The user selected warm graphite as the default direction. Warm gold is no longer an offered theme; the earlier exploration below is retained as historical evidence.
- Source baseline: `qa-evidence/graphite-baseline.png`; implementation: `qa-evidence/graphite-refined.png`. Both are 1487 × 1058 at a matching CSS viewport and screenshot density 1, with Getting Started.pdf selected.
- Inspected full paired evidence `refinement-comparison.png` and focused navigation/list comparison `refinement-detail.png`. The only added persistent toolbar control is Forward; typography, warm tokens, icon library, three-column proportions and list rhythm remain consistent. No new raster assets.
- Additional evidence: `refined-extract.png` shows two complete target paths; `refined-light.png` verifies the paired light theme at 1024 × 768. Captures taken during transition were replaced with stable-state captures after DOM checks.
- Browser checks passed: search → folder → Back restores search and selection; Forward restores the folder; Shift+Down extends selection; target preview responds to structure toggle; relative destination is rejected; New is disabled during a running task; cancel preserves an explicit cancelled task result.
- Creation checks: Windows-reserved name rejected; My-project.zip switched to 7Z previews My-project.7z; removing assets leaves two sources; completed task details retain the correct output name and count. A transient toast wait timed out; the persisted completed task was inspected instead, rather than claiming the timed-out check passed.
- Cancellation uses a cancellation symbol rather than a success check. Task simulations remain explicitly labelled and do not write files. Four helper tests cover suffix normalization, invalid names, absolute destination and per-item target preview.
- No remaining actionable P0/P1/P2 findings. Remaining P3: final branding and further user feedback on density. Browser console error query returned no errors during this iteration.

## Historical exploration record

## Target and evidence

- Visual truth: `reference/layout-option-1.png`, refined by the user's requested simpler top area, visible multiselect controls, and warmer palette; `reference/palette-option-2.png` informs the alternate warm-gold theme.
- Implementation: http://127.0.0.1:4173/ . Browser-rendered evidence: `qa-evidence/warm-graphite.png`.
- Matched viewport and pixels: source 1487 × 1058; implementation 1487 × 1058; CSS viewport 1487 × 1058, effective screenshot density 1. No geometric normalization required.
- State: project-kit.zip / docs, Getting Started.pdf selected. The implemented table uses correct alphabetical ordering with folders first; the generated image's inconsistent order is intentionally corrected.
- Full paired comparison: `qa-evidence/comparison.png` (2974 × 1090 including labels).
- Focused top/list comparison: `qa-evidence/comparison-detail.png`. Crops align the relevant navigation/table regions; their differing vertical positions are intentional because duplicated toolbars were removed.
- Additional captures: `warm-gold.png`, `light.png`, `extract-dialog.png`, `narrow.png` in `qa-evidence/`. Evidence stays local and is Git-ignored.

## Findings and iteration

1. Initial 12px filenames appeared too small relative to the selected reference at the same viewport (P2). Evidence: `initial-small-type.png`.
2. Increased the filename scale, ultimately to 16px, with 13px file types, 24px archive heading and 18px inspector heading. Recaptured the exact default state and inspected both full and focused paired comparisons. The final filename text is readable without expanding the toolbar again.
3. Added an accessible dialog name and retained native dialog focus trapping / Escape / focus return. This is behavior absent from the static source, not a design drift.
4. No remaining actionable P0/P1/P2 findings in the inspected states.

## Fidelity surfaces

- **Typography:** Windows system fallback stack, clear archive / filename / metadata hierarchy. Long filenames remain in a dedicated truncating cell rather than overlapping metadata. Main text is larger than the initial capture; small metadata is intentionally subordinate.
- **Layout:** three-column structure retained. Sidebar 228px and inspector 278px at the target viewport. Main top area has one identity/search row and one path/action row. One set of Back/Up controls. Persistent status bar remains visible; file content scrolls independently.
- **Colors:** warm neutral graphite, champagne emphasis, warm-gold alternative and light comparison are explicit requested changes. Selection bands and checkbox state are visible in each theme. No Mica or decorative background gradients.
- **Assets:** Phosphor line icons replace generated mock glyphs, including file and package symbols. No raster illustrations are required. No DBX screenshots, brand marks, actual databases, or user file contents are embedded in the UI.
- **Content:** fictional data throughout. Simulator states explicitly say no real files were written. Archive format metadata follows the selected sample extension. Default-app opening is feedback only, not actual OS integration.

## Browser interactions checked

- Initial PDF selection and inspector; selecting a second row gives 2 items / 260 KB.
- Header select-all gives 12 items, deselection clears them; Shift range selects three consecutive documents and shows 267 KB.
- Global search finds config.yml under config/; nonexistent search shows an actionable empty state.
- Double-click folder navigation and Back restore the previous directory; size sorting orders folders first and files by size.
- Double-click file shows the explicit simulated default-app action.
- Extraction dialog: editable destination, conflict selection, simulated cancel, simulated successful completion.
- Creation dialog: disabled creation until sources exist, name entry, 7Z selection, sample addition, successful simulation.
- Warm graphite, warm gold, light; compact-row checkbox; inspector hide/show.
- 1024 × 768: file type column hidden, no persistent controls clipped. 800 × 650: inspector hidden, file table and sidebar scroll, status and primary action remain available.
- Browser error logs queried after the main flows: no error entries.
- `npm run build`: passed. An initial build command issued from the repository root failed because there is no root package.json; using the explicit prototype prefix succeeded. This was a command-location failure, not an application build failure.

## Remaining scope and polish

- Final palette and density require user preference feedback; logo is a library package symbol, not final branding.
- No real extraction, creation, external opening, encoding conversion, disk access or engine integration. No claim of production accessibility certification or final desktop-framework selection.
- Native Windows window chrome and OS file pickers are deliberately not faked by non-working browser controls.

## Implementation checklist

- [x] Selected three-column composition with requested refinements.
- [x] Visible selection controls, mock workflows, keyboard focus and responsive layout.
- [x] Rendered reference comparison and typography refinement.
- [x] Browser error check and local build.
- [ ] User feedback on palette, density and daily interaction flow.
