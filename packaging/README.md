# Release candidates

`New-ReleaseCandidate.ps1` assembles the tested Windows host output in the
exact per-user Revit `Addins/2026` layout and includes a separate optional MCP
directory. It refuses a version that differs from the root `VERSION` file and
emits a ZIP plus a SHA-256 sidecar under `artifacts/`.

```powershell
dotnet restore Tripo.Revit.sln
dotnet build Tripo.Revit.sln --configuration Release --no-restore
dotnet test Tripo.Revit.sln --configuration Release --no-build
./packaging/New-ReleaseCandidate.ps1 -Version v0.1.0
```

The tag workflow uploads only a short-lived GitHub Actions artifact. It does
not create a GitHub Release, publish an installer, sign binaries, or establish
real Revit acceptance. Do not redistribute a candidate until this repository
has an explicit distribution license.
