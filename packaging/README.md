# Release candidates

`New-ReleaseCandidate.ps1` assembles tested Windows host outputs in the exact
per-user Revit `Addins/YYYY` layout (default years: 2025 and 2026) and includes
a separate optional MCP directory. It refuses a version that differs from the
root `VERSION` file and emits a ZIP plus a SHA-256 sidecar under `artifacts/`.

```powershell
dotnet restore Tripo.Revit.sln /p:RevitYear=2026
dotnet build src/Tripo.Revit/Tripo.Revit.csproj --configuration Release /p:RevitYear=2025
dotnet build Tripo.Revit.sln --configuration Release --no-restore /p:RevitYear=2026
dotnet test Tripo.Revit.sln --configuration Release --no-build
./packaging/New-ReleaseCandidate.ps1 -Version v0.1.0
```

Host build outputs land under
`src/Tripo.Revit/bin/Release/net8.0-windows/<RevitYear>/`. Override packaged
years with `-RevitYears 2026` when needed.

The tag workflow uploads only a short-lived GitHub Actions artifact. It does
not create a GitHub Release, publish an installer, sign binaries, or establish
real Revit acceptance. Candidate redistribution is governed by the Apache License, Version 2.0;
see the repository root [`LICENSE`](../LICENSE) and [`NOTICE`](../NOTICE).
