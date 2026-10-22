# AileArc Native SDK validation

Independent, read-only warm graphite prototype. It does not replace either the
WinUI client or the WPF/WebView2 validation host. Native SDK is experimental.

## Build

From the repository root, with PowerShell 7, Node.js 24+ and Python 3:

```powershell
./scripts/Build-NativeValidation.ps1
# If the system's node is older, supply a Node 24+ executable:
./scripts/Build-NativeValidation.ps1 -NodePath 'C:/path/to/node.exe'
./scripts/Start-NativeValidation.ps1
./scripts/Start-NativeValidation.ps1 -Archive 'D:/Archives/example.zip'
```

Build dependencies are isolated in `.tools`, output in `.artifacts/native-validation`.
The source revision, Zig version, compiler version and font hash are pinned.
No runtime association, trusted certificate, Developer Mode or system runtime is
installed or changed. The generated font and dependencies are not committed.

## What is being compared

- The native executable uses Native markup and compiled TypeScript, without a
  WebView. Windows rendering is checked from the runtime snapshot, not inferred
  from the manifest's legacy `gpu_backend` spelling.
- The 1,000/100,000 sidebar modes are **generated rendering data**. Virtual windows
  construct only the visible rows. They do not measure archive parsing.
- A real archive is scanned by the existing .NET Core/isolated Worker. The local
  adapter reuses `ArchivePresentation` and its 200-row bounded pages, so the real
  archive comparison uses the same index/query implementation as WebView2.
- The adapter binds IPv4 loopback only, uses a per-launch random header token,
  rejects browser Origin headers and exposes one read-only query endpoint. There
  is no arbitrary-path, execution, extraction or file-writing endpoint. This is
  validation IPC, not a production IPC decision or OS sandbox.
- Changing pages clears selection. Checkboxes toggle independently; select-all
  is scoped to the current page (or current generated result). A focused row and
  selection are separate. Search commits on Enter/button after IME composition.
- Real archive selection is supplied at launch. Password/encoding dialogs,
  extraction, archive creation, external editing, full Shell integration and
  localization switching are outside this experiment.

## Reproduction and evidence

```powershell
python scripts/Create-UIValidationSamples.py .artifacts/ui-comparison/samples
./scripts/Measure-UIValidation.ps1 -HostKind Native -Archive .artifacts/ui-comparison/samples/entries-1000.zip
./scripts/Measure-UIValidation.ps1 -HostKind WebView2 -Archive .artifacts/ui-comparison/samples/entries-1000.zip
```

Run hosts sequentially, wait for background builds to finish and verify a populated
archive page before interpreting memory samples. Measurements include all observed
descendants and the .NET adapter; shared runtime files do not make process memory
free. Working-set sums double-count shared pages; private bytes measure commitment.
Warm launches do not establish cold-start time. Native currently scans before
opening its window, while WebView2 opens its shell before scanning.

Detailed results and outstanding gates: `docs/Native_UI_Validation.md`.

## Upstream portability findings

Native SDK 0.11.0 npm packaging omits a test fixture unconditionally embedded by
its build script. Use the matching full source at
`16f29572307c2cc133907537c4dfd98046d4e0f4`. The small patch in
`scripts/native-sdk-windows-path.patch` normalizes the theme-type path comparison
and makes alias staging accept CRLF. No generated ABI output is hand-edited.

The uniform virtual list uses an extent estimator returning 40 because the
fixed-extent-only adapter generated unused Zig parameters/locals in this version.
SDK documentation and the installed package differ in places; the prototype was
built against the pinned package's actual interfaces.
