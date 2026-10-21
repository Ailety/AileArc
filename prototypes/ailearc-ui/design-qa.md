# AileArc prototype QA — 2026-10-21

final result: passed

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
