# Testing and evidence

This repository ships only the Revit 2026 adapter and its matching local
sidecar/MCP executable.

## Windows full gate

Run from the repository root on Windows:

```powershell
dotnet restore Tripo.Revit.sln
dotnet build Tripo.Revit.sln --configuration Release --no-restore
dotnet test Tripo.Revit.sln --configuration Release --no-build
```

The full gate must produce:

```text
src/Tripo.Revit/bin/Release/net8.0-windows/Tripo.Revit.dll
src/Tripo.Revit/bin/Release/net8.0-windows/Tripo.Revit.addin
src/Tripo.Revit/bin/Release/net8.0-windows/sidecar/Tripo.Revit.Mcp.dll
src/Tripo.Revit.Mcp/bin/Release/net8.0/Tripo.Revit.Mcp.dll
```

## Portable core gate

The macOS gate intentionally excludes the WindowsDesktop/Revit host project:

```bash
dotnet restore Tripo.Revit.Portable.slnf /p:SkipRevitHostBuild=true
dotnet build Tripo.Revit.Portable.slnf --configuration Release --no-restore \
  /p:SkipRevitHostBuild=true
dotnet test Tripo.Revit.Portable.slnf --configuration Release --no-build \
  /p:SkipRevitHostBuild=true
```

It still exercises the bridge, host UI workflow, Tripo API workflow, raw Revit
sidecar/MCP stdio handshake, host-control health/shutdown, paid-operation
journal, artifact pipeline, and `.addin` identity contract. It does not compile
the WPF/Revit host or test the copied host-package sidecar layout.

The automated suite covers:

- authenticated, bounded bridge and host-control transports;
- exact host PID and document-session binding;
- credential precedence and redaction, macOS/Windows native-store selection
  without file fallback, and an opt-in isolated Windows native
  write/read/delete canary;
- paid-operation write-ahead journal, replay, conflict, lock, and
  `outcome_unknown` refusal to resend;
- explicit successful-response `code` validation plus exact round-trip of
  current `task_...` and canonical lowercase UUID task identities;
- local and URL image staging for PNG/JPEG/WebP, byte/signature agreement,
  redirect and private-network rejection (including IPv4-compatible IPv6 and
  NAT64), streaming byte caps, timeout/cancellation cleanup, signed download,
  ZIP extraction, bundle hashing, OBJ/MTL parsing, coordinate conversion, and
  parser limits;
- image panel-session UUID/receipt/hash/model binding, same-UUID
  `image_file_token_persisted` retry, upload-vs-generation rejection semantics,
  task-type checking, descriptor cleanup, and schema-v4 recovery without
  paths, URLs, bytes, or opaque descriptors;
- host UI state and presentation contracts, inline input validation, honest
  task-progress mapping, diagnostic content, snapshot-bound recovery-review
  receipts, and stable UUID retry eligibility;
- MCP schema plus real stdio initialization/tool-list handshakes;
- Revit `.addin` AddInId, assembly path, application class, and vendor identity;
- Windows-only CI additionally compiles the Revit/WPF host and exercises
  packaged-sidecar smoke tests. It also enables
  `TRIPO_RUN_WINDOWS_CREDENTIAL_MANAGER_CANARY=1`, which uses a synthetic value
  under a unique `TripoMCPs/Tests/.../<pid>/<guid>` target for `CredWriteW`,
  `CredReadW`, and `CredDeleteW`. The canary never reads `TRIPO_API_KEY` or the
  production `TripoMCPs/TripoV3/<username>` target.

Portable presentation tests verify the strings and action state supplied to
the WPF layer. They do not prove that Windows renders text as copyable, that a
dialog defaults to the intended button, or that UI Automation exposes the
expected label and help relationships.

Generated `bin/` and `obj/` files are ignored. A clean test receipt also
requires `git diff --check` and a clean `git status`.

## Evidence boundaries

A green portable gate is not Revit acceptance. Real Revit 2026 validation on
Windows remains required:

The isolated Windows CI credential canary proves only the native API in an
ephemeral runner profile. Cleanup runs in `finally`, although a hard runner
termination can still leave a clearly prefixed synthetic orphan. It is not
evidence for Revit UI, a production user profile, or a compatible paired Rhino
deployment.

1. Load the `.addin` and open **Add-Ins → Tripo → Tripo 3D**.
2. Exercise Windows Credential Manager without saving a key into `.rvt` or
   `.rfa`.
3. Verify all document work runs through `ExternalEvent`, the window remains
   responsive, and document switches fail closed.
4. Run text, local PNG/JPEG/WebP, and public-HTTPS image generation; verify the
   local in-memory preview, its confirmation SHA/length, replacement-file
   refusal before paid dispatch, oversized-dimension preview refusal, URL
   no-preview boundary, status, conversion,
   DirectShape import, and family import with explicit cost confirmations.
5. Deliberately lose a paid response and recover with the displayed UUID
   without issuing a replacement POST.
6. Verify transaction rollback, Undo, design-coordinate limits, family-template
   resolution, saved `.rfa`, idempotent replay, materials, and appearance assets.
7. Verify package layout and upgrade/rollback behavior from a clean checkout.

The WPF window also needs a separate visual and accessibility pass:

1. At 100%, 150%, and 200% Windows scaling, resize the tool window down to its
   minimum size. Verify that stage content reflows, action buttons wrap, and no
   prompt, status, checkbox, or recovery text is clipped.
2. Test keyboard-only traversal, default/cancel behavior in both dialogs, focus
   return after dialogs close, and copying every document, operation, task, and
   receipt ID.
3. With Windows High Contrast and Narrator enabled, verify the three numbered
   stages, field labels, live operation status, inline validation, progress
   values, expanders, and disabled-action explanations.
4. Confirm that no progress bar appears before a real task progress value is
   returned; out-of-range provider values must remain visible only as raw
   diagnostic evidence.
5. Exercise the friendly **Revit family (recommended)** and
   **DirectShape mesh** choices, with and without baked materials, and compare
   the visible import receipt to the actual created Revit element.
6. Change the recovery files after inspection but before acknowledgement. The
   final attempt must reload and refuse the changed snapshot; the refreshed
   snapshot must be inspected again before it can be archived. Also change a
   paid-journal status after inspection and verify that the one-time review
   receipt is rejected.
7. Make one recovered paid-status lookup fail. Verify the header says
   **Manual review required**, the failure details remain copyable, and the
   additional failure-review checkbox is required.
8. Before claiming deployed cross-host credential protection, pair this Revit
   build with a compatible Rhino build carrying the same conservative
   paid/import recovery filter. Verify both mutation directions; the paired
   source has parity, but Revit-only or legacy-Rhino evidence does not prove
   the deployed pair.
9. Exercise a URL whose redirect changes from public to loopback/private and a
   DNS result containing private, IPv4-compatible, or NAT64 addresses. Staging
   must fail before any paid image request, and no URL may appear in recovery
   JSON, operation journals, or UI receipts.

Static checks, portable tests, Windows compilation, real Revit interaction,
visual acceptance, signing, installer validation, and public release are
distinct evidence classes.
