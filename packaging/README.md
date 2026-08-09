# Release candidates

`New-ReleaseCandidate.ps1` assembles tested Windows host outputs in the exact
per-user Revit `Addins/YYYY` layout (default years: 2025 and 2026) and includes
a separate optional MCP directory. It refuses a version that differs from the
root `VERSION` file and emits a ZIP plus a SHA-256 sidecar under `artifacts/`.

```powershell
dotnet restore src/Tripo.Revit/Tripo.Revit.csproj /p:RevitYear=2025
dotnet build src/Tripo.Revit/Tripo.Revit.csproj --configuration Release --no-restore /p:RevitYear=2025
dotnet restore Tripo.Revit.sln /p:RevitYear=2026
dotnet build Tripo.Revit.sln --configuration Release --no-restore /p:RevitYear=2026
dotnet test Tripo.Revit.sln --configuration Release --no-build --no-restore /p:RevitYear=2026
./packaging/New-ReleaseCandidate.ps1 -Version v0.1.1
```

Host build outputs land under
`src/Tripo.Revit/bin/Release/net8.0-windows/<RevitYear>/`. Override packaged
years with `-RevitYears 2026` when needed.

The `release-candidate` workflow accepts only an existing canonical tag whose
commit is checked out and whose version matches `VERSION`. The read-only build
job binds the tag ref/object SHA, peeled commit, event commit, and archive hash
into an uploaded JSON receipt. A separate job downloads only those three
candidate files, checks the remote tag and receipt again without checking out or
executing repository code, and only then receives `contents: write` to create a
new release. It fails if the GitHub Release already exists and never edits or
overwrites release assets. Create and review a new tag before running the
workflow. No source revision has release provenance merely because its build
passes, and `v0.1.0` remains bound to its original commit and assets.

`Test-ReleaseIdentity.ps1` is an optional, read-only manual preflight for an
existing local tag. Set `GH_TOKEN`, then pass the tag and `owner/repository`; use
`-RequireReleaseAbsent` before proposing a new release. The workflow keeps its
publish checks inline so the write-authorized job never executes code from the
tag checkout.

The workflow also uploads a short-lived GitHub Actions artifact containing the
ZIP, SHA-256 sidecar, and source-identity manifest. A tag-triggered or explicitly
dispatched run creates a GitHub Release only after the second identity gate. It
does not publish an installer, sign binaries, or establish real Revit
acceptance. Candidate redistribution is governed by the Apache License, Version
2.0; see the repository root [`LICENSE`](../LICENSE) and
[`NOTICE`](../NOTICE).

The GitHub tag-ref check and release creation are separate API operations, and
asset upload can fail after a draft is created. Production release governance
must therefore protect release tags (and enable immutable releases where
available). If creation fails, inspect and resolve any partial draft manually;
the workflow will refuse to overwrite it on a retry.
