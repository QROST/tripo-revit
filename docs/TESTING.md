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
- credential precedence, native secret-store behavior, and redaction;
- paid-operation write-ahead journal, replay, conflict, lock, and
  `outcome_unknown` refusal to resend;
- image transfer, signed download, ZIP extraction, bundle hashing, OBJ/MTL
  parsing, coordinate conversion, and parser limits;
- host UI state, recovery hints, explicit paid confirmations, and stable UUID
  retries;
- MCP schema plus real stdio initialization/tool-list handshakes;
- Revit `.addin` AddInId, assembly path, application class, and vendor identity;
- Windows-only Revit host compilation and packaged-sidecar smoke tests.

Generated `bin/` and `obj/` files are ignored. A clean test receipt also
requires `git diff --check` and a clean `git status`.

## Evidence boundaries

A green portable gate is not Revit acceptance. Real Revit 2026 validation on
Windows remains required:

1. Load the `.addin` and open **Add-Ins → Tripo → Tripo 3D**.
2. Exercise Windows Credential Manager without saving a key into `.rvt` or
   `.rfa`.
3. Verify all document work runs through `ExternalEvent`, the window remains
   responsive, and document switches fail closed.
4. Run text generation, status, conversion, DirectShape import, and family
   import with explicit cost confirmations.
5. Deliberately lose a paid response and recover with the displayed UUID
   without issuing a replacement POST.
6. Verify transaction rollback, Undo, design-coordinate limits, family-template
   resolution, saved `.rfa`, idempotent replay, materials, and appearance assets.
7. Verify package layout and upgrade/rollback behavior from a clean checkout.

Static checks, portable tests, Windows compilation, real Revit interaction,
visual acceptance, signing, installer validation, and public release are
distinct evidence classes.
