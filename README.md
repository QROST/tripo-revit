# Tripo-Revit

English | [简体中文](./README.zh-CN.md)

Tripo-Revit is an independent community adapter for Autodesk Revit 2026. AEC
users can run the complete text-to-model workflow from a modeless WPF window;
agentic clients can use the same workflow through MCP. Validated OBJ output —
geometry only, or with baked-diffuse materials and textures — is imported into
the writable Revit project that is active when import is requested, as a Generic
Models `DirectShape` or as a generated, loaded, and placed Family instance.
Generation and conversion are not bound to a Revit project, so one successful
conversion can be imported into multiple projects with a fresh import UUID for
each target.

It is not an official Tripo or Autodesk product.

```text
Revit WPF window            MCP client
       ↕ host-control          ↕ stdio
       Tripo.Revit.Mcp sidecar / server ── Tripo v3 HTTPS API
                    ↕ authenticated protocol-v3 host bridge
                 Tripo.Revit add-in
                    ↕ bounded ExternalEvent FIFO + transactions
                 exact active Revit project document
```

The sidecar is the only process that resolves, stores, or uses the Tripo API
key. The add-in's password dialog only forwards a transient value over the
authenticated local control channel and clears the field; it does not write
the key to `.rvt`, `.rfa`, or Revit settings.

> **Current status:** the add-in targets Revit 2026 on Windows x64. Portable
> host-control/workflow/MCP/process tests and package-compilation gates exist.
> The Windows CI lane is configured to compile the modeless WPF window, ribbon
> command, sidecar
> launcher, text workflow, credential dialog, and bundled sidecar layout, and
> to write/read/delete a synthetic credential under an isolated test target.
> Real Revit loading, WPF/owner behavior, production-user Credential Manager
> interaction,
> transaction/Undo, scale/orientation, performance, and visual acceptance are
> still open gates. There is no installer, signing, or automatic update
> mechanism.

## Prerequisites

- Windows x64 with Autodesk Revit 2026.
- .NET 8 SDK to restore and build the projects. The repository selects
  `8.0.100` with `latestFeature` roll-forward inside .NET 8. Restore requires
  NuGet access.
- A .NET 8 runtime to run the framework-dependent MCP server. The SDK includes
  this runtime.
- An MCP client that supports stdio servers, only for the optional MCP path.
- A Tripo v3 API key for remote generation and conversion.
- A writable, non-Family Revit project for import.
- Revit, the panel sidecar, and any MCP server must run as the same Windows
  user.

The host project targets `net8.0-windows`, `x64`, and the pinned
`Autodesk.Revit.SDK` package version `2026.0.0.9999` for compile-time
references. The repository does not record a verified package hash or
provenance receipt. For a production build, verify that dependency or replace
it with controlled Revit 2026 installation/SDK references.

## Build

Run from the repository root in PowerShell:

```powershell
dotnet restore src/Tripo.Revit/Tripo.Revit.csproj
dotnet restore src/Tripo.Revit.Mcp/Tripo.Revit.Mcp.csproj

dotnet build src/Tripo.Revit/Tripo.Revit.csproj `
  --configuration Release `
  --no-restore

dotnet build src/Tripo.Revit.Mcp/Tripo.Revit.Mcp.csproj `
  --configuration Release `
  --no-restore
```

Outputs:

```text
src/Tripo.Revit/bin/Release/net8.0-windows/
src/Tripo.Revit.Mcp/bin/Release/net8.0/
```

A non-Windows build is compile-time evidence only. Loading and using the add-in
requires Revit 2026 on Windows.

Building the host project also builds the matching sidecar and copies its
complete output into
`src/Tripo.Revit/bin/Release/net8.0-windows/sidecar/`. The separate
`src/Tripo.Revit.Mcp/bin/Release/net8.0/` output is needed only for the optional MCP
path. Bridge protocol v3 and host-control protocol v7 have no
backward-compatibility shim, so deploy all components from the same repository
revision.

## Install the Revit add-in

Close Revit before installing or replacing files.

The per-user deployment layout is:

```text
%APPDATA%\Autodesk\Revit\Addins\2026\
├── Tripo.Revit.addin
└── TripoRevit\
    ├── Tripo.Revit.dll
    ├── Tripo.Bridge.dll
    ├── Tripo.HostUi.dll
    ├── sidecar\
    │   ├── Tripo.Revit.Mcp.exe
    │   ├── Tripo.Revit.Mcp.dll
    │   ├── Tripo.Revit.Mcp.deps.json
    │   ├── Tripo.Revit.Mcp.runtimeconfig.json
    │   └── sidecar dependencies
    └── other files from the same host build output
```

The manifest must remain directly under the `2026` directory. Its relative
assembly entry is:

```xml
<Assembly>TripoRevit\Tripo.Revit.dll</Assembly>
```

From the repository root, this PowerShell example populates that layout:

```powershell
$source = (Resolve-Path "src\Tripo.Revit\bin\Release\net8.0-windows").Path
$addinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2026"
$pluginDirectory = Join-Path $addinRoot "TripoRevit"

New-Item -ItemType Directory -Force $pluginDirectory | Out-Null
Get-ChildItem -LiteralPath $source |
  Where-Object { $_.Name -ne "Tripo.Revit.addin" } |
  Copy-Item -Destination $pluginDirectory -Recurse -Force
Copy-Item (Join-Path $source "Tripo.Revit.addin") `
  (Join-Path $addinRoot "Tripo.Revit.addin") `
  -Force
```

Copying the full output is safer than guessing a minimum dependency set.
`RevitAPI.dll` and `RevitAPIUI.dll` are marked `Private=false` and are supplied
by Revit, so the build should not deploy local copies.

The example overwrites same-named files but does not remove files left by an
older revision. For a clean upgrade, keep Revit closed, move the existing
`TripoRevit` directory aside for rollback, and then populate a new directory.

Autodesk documents both per-user and all-users `.addin` registration
locations: [Registration of add-ins](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_Registration_of_add_ins_html.html).
This repository's example deliberately uses the per-user location and requires
no installer.

Restart Revit after installation. The bridge starts automatically as an
`IExternalApplication`, and the add-in creates **Add-Ins → Tripo → Tripo 3D**.
That button opens or activates one modeless workflow window. If startup fails,
Revit displays a **Tripo MCP Bridge** dialog.

## Configure the optional MCP server

Keep the complete contents of:

```text
src\Tripo.Revit.Mcp\bin\Release\net8.0\
```

together in a stable local directory. The adjacent assemblies, `.deps.json`,
and `.runtimeconfig.json` are required; do not deploy only
`Tripo.Revit.Mcp.exe` or `.dll`.

The server uses MCP over stdio. It is not an HTTP service and has no port
configuration. The portable invocation is:

```powershell
dotnet C:\absolute\path\to\Tripo.Revit.Mcp.dll
```

The generated Windows apphost can also be used:

```powershell
C:\absolute\path\to\Tripo.Revit.Mcp.exe
```

Normally the MCP client starts this process. Running it manually only makes it
wait for a stdio handshake.

The following is a common configuration shape for clients that use
`mcpServers`, `command`, `args`, and `env`. Adapt it to your client's schema and
secret mechanism:

```json
{
  "mcpServers": {
    "tripo-revit": {
      "command": "dotnet",
      "args": [
        "C:\\Tools\\Tripo-Revit-Mcp\\Tripo.Revit.Mcp.dll"
      ],
      "env": {
        "TRIPO_API_KEY": "REPLACE_USING_YOUR_CLIENT_SECRET_MECHANISM"
      }
    }
  }
}
```

Use absolute paths; Windows paths in JSON require escaped backslashes. If a GUI
MCP client has a restricted `PATH`, use the absolute path to `dotnet`.

### Environment variables

| Variable | Where to set it | Requirement |
| --- | --- | --- |
| `TRIPO_API_KEY` | Sidecar / MCP server | Optional environment-supplied key. It overrides session and stored keys. The window can set a key without this variable. |
| `TRIPO_MODEL` | Sidecar / MCP server | Optional text-generation model identifier. When set, it overrides the panel Settings choice. The default is `v3.1-20260211`; an override must match `[A-Za-z0-9._-]{1,64}`, is returned by the text-task receipt, and is part of the text-task paid request identity. Set it before Revit starts for the panel-launched sidecar. |
| `TRIPO_HOST_PID` | MCP server only | Required when more than one live Revit bridge exists. Must be a positive integer. |
| `TRIPO_LOCAL_DATA_DIR` | Revit and sidecar / MCP server | Optional absolute, private, stable local path. Every participating process must resolve exactly the same value. |
| `TRIPO_SIDECAR_PATH` | Revit process only | Optional absolute development override to the matching `Tripo.Revit.Mcp.dll` or native apphost. Normal deployment uses the copied install-relative `sidecar/`; set an override before Revit starts. |
| `TRIPO_REVIT_FAMILY_TEMPLATE` | Revit process only | Optional absolute path to a `.rft` family template used for `family`-mode imports. Must be set before Revit starts. When unset, relative, or missing, the add-in continues by probing exactly `Metric Generic Model.rft`, `Generic Model.rft`, `英制常规模型.rft`, and `公制常规模型.rft` under Revit's `FamilyTemplatePath`, then the first 32 immediate subdirectories in ordinal order. Other locales should set a valid absolute path explicitly; `family_template_unavailable` is returned if none is found. |

For the window path, use **API key…**: leave **Save in this user's OS
credential store** checked for Windows Credential Manager, or uncheck it for
sidecar-process memory only. The UI reports only `environment`, `session`,
`store`, or `none`, never the key. For the MCP path, prefer the client's
credential store or inherited process environment. Configuration files may
store `env` values as plaintext, and `${NAME}` interpolation is client-specific.
This repository does not load `.env` files. Replacing the effective key changes
paid-operation identity and can make same-UUID recovery fail closed for
unfinished window or MCP operations. Reconcile every unfinished paid UUID
before rotating a key.

Persistent window keys are Windows Generic Credentials under the target
`TripoMCPs/TripoV3/<username>`. They are not written to `.rvt`, `.rfa`, Revit
settings, or a temporary file. Do not create a temp key file: uncheck the
checkbox for sidecar-process memory only, keep it checked for the native
current-user store, or use an MCP client's secret/environment mechanism.

The safest setup is to leave `TRIPO_LOCAL_DATA_DIR` unset in both processes.
They then share `%LOCALAPPDATA%\TripoMCP`.

If you customize the directory:

- set the identical value in the Revit process environment before Revit starts
  and in the MCP server environment;
- use an absolute, private path on a stable local filesystem;
- do not use NFS/SMB; and
- do not move or delete its `bridges`, `controls`, `staging`,
  `image-transfers`, `operations`, `secrets`, `ui-recovery`, or `families`
  content during recovery.

Setting this variable only in the MCP client makes the server and Revit use
different discovery/staging roots and prevents a correct bridge connection.

`image-transfers` may contain a private copy of an MCP-selected PNG/JPEG until
a durable file-token or upload-ambiguity checkpoint exists. Preserve it with
the paid-operation journal during recovery.

## Use the Revit window

1. Start Revit 2026, open the writable target project, and choose
   **Add-Ins → Tripo → Tripo 3D**. Repeated clicks activate the same modeless
   window. If it owns workflow state, clicking the close button hides it
   silently; use the ribbon command to show it again.
2. The window automatically connects to the active project. Enter a prompt and
   click **Create in Revit**. One credit confirmation covers generation and
   OBJ conversion; the panel then auto-polls, converts, and imports without
   manual Refresh / Convert / Import clicks. Progress shows
   Generating → Converting → Importing on a single bar.
3. If no key is usable, **Create** opens the API-key dialog (also available
   under **Settings**). Create keys at
   [Tripo Platform](https://platform.tripo3d.ai/api-keys).
4. Open **Settings** for Tripo model (documented versions loaded at panel
   start / Connect), face limit, materials, object name, import type
   (family or DirectShape), placement (**project origin** or **pick point**),
   and optional nearest-level association for family instances. Category stays
   Generic Models and format stays OBJ. Preferences persist under the local
   data root (`ui-settings/revit-panel.json`).
   Tripo does not publish a list-models HTTP API; the picker uses the curated
   documented catalog. `TRIPO_MODEL` still overrides the UI choice when set for
   the sidecar.
5. Use **Advanced** for Connect/Refresh, per-stage Generate/Convert/Import,
   recovery tools, and expandable durable UUIDs / diagnostics. MCP clients
   continue to use the staged tool surface.

Generation and conversion state survives project switches. An import is bound
only after its fresh UUID is prepared for the currently active target;
switching is blocked only while that import's dispatch outcome is unresolved.
Activate another writable project and reconnect (Advanced) to import the same
conversion there under a new import UUID.

The modeless window never accesses `Document` or `UIApplication` directly for
workflow state; context and mutation still travel sidecar → host bridge →
bounded `ExternalEvent` (pick-to-place uses the API thread after commit). After
a lost response, Advanced stage actions still require **Refresh**. A retry
becomes available only when the paid-operation journal says creation can
resume. **New** remains disabled until the current workflow is explicitly
reset. Environment-provided keys cannot be replaced from the panel. An accepted
or ambiguous dispatch remains account-bound until reset: replacement keys are
session-only, must belong to the same Tripo account, and an unresolved paid
UUID without a durable task requires the exact original key. A durable
`request_rejected` receipt is different because it proves no remote task was
created: generation rejection clears generation and every downstream stage,
while conversion rejection clears only conversion/import and preserves the
successful account-bound generation. Correct the credential and prepare a new
UUID for the rejected stage. Resetting a resolved workflow requires a
default-No confirmation and does not cancel remote tasks, remove journal
evidence, or delete imported Revit objects.

Before dispatch, the window's shared state layer atomically writes a private
recovery hint under
`%LOCALAPPDATA%\TripoMCP\ui-recovery\revit\<recovery-id>.json`
(or the equivalent `TRIPO_LOCAL_DATA_DIR` root). The hint contains UUIDs, the
durable generation task used as the conversion source, durable result task IDs
when known, and the minimum import retry parameters. It does not contain the
prompt, API key, Authorization header, URL, or arbitrary path.

The independently identified hint remains through a successful import until
the live workflow is explicitly reset. After a Revit exit or crash, the next
window shows stale recovery IDs and blocks new workflows. A hint owned by
another Revit process is conservatively
blocking because panel-session liveness is not guessed across processes.
Recovery must happen in that owner process, or after its exit can be verified.
This Revit build also refuses an API-key change when it observes any recorded
UI paid hint, unconfirmed import, unverifiable foreign-owner record, or invalid
recovery storage from Rhino or Revit. Symmetric protection against a
Rhino-initiated key change is present only when the separately built/deployed
Rhino panel carries the matching conservative filter. The paired source
revision now does, but a legacy Rhino binary can still bypass that direction;
compatible dual-host deployment remains a cross-host release dependency. A
root-global UI intent lease serializes cross-panel credential-recovery scans,
key-mutation requests, and paid dispatch calls. A separate private sidecar
execution lease holds the actual key mutation and each paid UI or standalone
MCP workflow from credential-derived fingerprinting through its durable task,
definitive `request_rejected`, or ambiguous-outcome journal checkpoint, even if
the UI pipe disconnects. Only one key mutation or paid
create/convert is admitted at a time; retry a contending request with the same
UUID after the active operation checkpoints. **Inspect recovered IDs** only
queries local `operation_status`; it does not resend a paid call or import.
Reconcile import manually with the displayed same UUID. After checking every
ID, **Acknowledge reviewed IDs…** requires the applicable paid/import review
checkboxes. The inspection issues a one-time receipt bound to that exact
recovery snapshot. Before archiving, the panel reloads the recovery set and
rechecks every successful paid-journal status; if either changed, the final
attempt is refused and the refreshed snapshot must be inspected again.
Inspection failures are labelled **Manual review required** and require an
additional explicit failure-review checkbox. Invalid, oversized,
unknown-schema, or symlinked hints remain blocked for manual inspection; a
custom Windows data root must retain
current-user-only ACLs. The paid-operation journal—not the hint—is
authoritative. The Revit window remains text-only; local-image creation is
available through the optional MCP tools.

## Start and verify MCP

For the optional MCP path:

1. Install the add-in while Revit is closed.
2. Start Revit 2026.
3. Open the writable project that should receive the model.
4. Start or restart the MCP client so the server receives the intended
   environment.
5. Confirm that the client lists the eight tools below.
6. Call `tripo_host_context`.

A successful context receipt proves that the MCP server reached Revit and
returns:

- `host: "revit"`;
- the actual Revit version and process ID;
- the active document title and units;
- the exact ephemeral `documentSessionId`; and
- the supported host capabilities.

Use `documentSessionId` only for an import into that exact active project.
Generation, image upload/generation, conversion, task status, and operation
status are project-independent. Call `tripo_host_context` again immediately
after every target change and before preparing that import.

There is no HTTP health endpoint. If multiple Revit processes are running,
discovery returns `host_ambiguous` instead of guessing. Set `TRIPO_HOST_PID` to
the intended Revit PID and restart the MCP server.

## MCP tools

The MCP front door exposes the same shared workflow as these eight tools:

| Tool | Main arguments | Effect |
| --- | --- | --- |
| `tripo_host_context` | none | Reads the connected Revit process and exact active-document session. No Tripo API call. |
| `tripo_task_status` | `taskId` | Queries one existing Tripo task. |
| `tripo_operation_status` | `operationId` | Reads a durable local paid-operation record. No Tripo or Revit call. |
| `tripo_create_text_task` | `prompt`, `faceLimit`, `withMaterials`, `operationId`, `confirmExternalCost`, optional `model` | Creates one project-independent text-to-model task. `withMaterials=true` requests textured PBR generation (`texture`/`pbr`); `false` stays geometry-only. Optional `model` selects a Tripo snapshot (e.g. `P1-20260311`); omitted uses `TRIPO_MODEL` or the catalog default. May consume credits. |
| `tripo_stage_local_image` | `localImagePath` | Validates and privately snapshots one local PNG/JPEG and returns an opaque descriptor. No Tripo call. |
| `tripo_create_image_task` | `transferId`, `sha256`, `byteLength`, `mediaType`, `faceLimit`, `withMaterials`, `operationId`, `confirmExternalCost` | Uploads one staged image and creates a project-independent image-to-model task with durable upload/generation checkpoints. Copy the four descriptor fields exactly from `tripo_stage_local_image`. May consume credits. |
| `tripo_create_obj_conversion` | `sourceTaskId`, `faceLimit`, `withMaterials`, `operationId`, `confirmExternalCost` | Creates one project-independent OBJ conversion. `withMaterials=true` requests an OBJ bundle with a baked-diffuse MTL and image textures (`bake=true`); `false` converts geometry only. May consume credits. |
| `tripo_import_obj_task` | `conversionTaskId`, `name`, `documentSessionId`, `operationId`, `importMode` (default `native`), `applyMaterials` (default `false`) | Downloads, validates, and imports a successful OBJ conversion as a `DirectShape` or a Family instance. |

Input boundaries:

- `prompt`: 1–1024 characters;
- `faceLimit`: 500–200000;
- imported `name`: 1–128 characters;
- task IDs are used exactly as returned by Tripo: current `task_...`
  identifiers and canonical lowercase UUID responses are accepted;
- import `documentSessionId` must be the exact UUID from the immediately
  preceding `tripo_host_context`;
- each `operationId` is a caller-generated UUID;
- `importMode` is `native`, `mesh`, or `family`; this build rejects `instance` with
  `import_mode_unsupported`. `native` resolves to `family`.
- `applyMaterials=true` fails closed if the converted bundle has no MTL.

`confirmExternalCost=true` is valid only after the user explicitly accepts the
possible external charge.

## Typical workflow

1. Choose one generation branch:
   - text: generate UUID A and, after explicit cost confirmation, call
     `tripo_create_text_task`;
   - local image: call `tripo_stage_local_image`, then generate UUID A and,
     after explicit cost confirmation, call `tripo_create_image_task`, copying
     the returned descriptor's `transferId`, `sha256`, `byteLength`, and
     `mediaType` into the four same-named arguments.
2. Poll its returned task ID with `tripo_task_status` until it reports
   `success` or a terminal failure. Stop on `failed`, `cancelled`, `banned`, or
   `expired`.
3. Generate UUID B and, after a second explicit cost confirmation, call
   `tripo_create_obj_conversion`.
4. Poll the returned conversion task until it reports `success` or a terminal
   failure.
5. Activate the writable target project, call `tripo_host_context`, generate
   UUID C, and call `tripo_import_obj_task` with that returned
   `documentSessionId`, choosing `importMode` and `applyMaterials` as needed.
6. Inspect the host receipt and the created `DirectShape` or Family instance.
   To reuse the conversion in another project, repeat step 5 with the new
   context and a new import UUID.

For a material-bearing import, set `withMaterials=true` on both paid creation
stages and `applyMaterials=true` on import. Keep all three false for a
geometry-only workflow.

The two paid creation calls and the host import must use three different
caller-owned UUIDs. A committed import normally returns
`transactionStatus: "committed"`; an identical retry may return
`"already_exists"`.

If a paid-stage response is lost, first use `tripo_operation_status` to inspect
its local record. Retry with the original UUID, identical explicit arguments,
and API key only when the journal says creation can resume; a text-task retry
must also keep the same effective model.

If an operation is `outcome_unknown`, do not automatically resend it or create
a replacement UUID. Preserve the journal and inspect Tripo task or billing
history manually.

If an operation is `request_rejected`, the provider definitively rejected the
request before creating a task. Correct the credential and prepare a new UUID;
do not retry the rejected UUID.

Image creation checkpoints upload and generation separately. A durable
`file_token` resumes generation without re-uploading. An ambiguous upload or
generation records its stage and refuses automatic resend; preserve
`image-transfers/` and the journal until manual reconciliation.

Import recovery is deliberately different. An unresolved retry reuses the
import UUID, conversion task and artifact content, name, resolved mode, and
materials flag for the same target project. If Revit restarted, reopen that
target, call `tripo_host_context`, and pass the new `documentSessionId`; the
host fingerprint excludes that ephemeral session ID while the active-session
check still fails closed. A deliberate import into another project uses a new
import UUID. For `already_exists` to survive the application restart, save the
Revit project after the original import; if unsaved changes were lost, the
retry can commit the import again because no persisted element remains.

## Revit import behavior

- All Revit API reads and writes run through `ExternalEvent`.
- Host requests use a bounded FIFO; a full queue fails with `host_busy`.
- The active document is checked before staging and again immediately before
  mutation.
- The converted OBJ, optional MTL, and PNG/JPEG textures form a
  content-addressed bundle. Every kept entry is checked against the manifest;
  a bundle keeps at most 32 entries, each at most 128 MiB, with a 256 MiB
  aggregate limit.
- Imports require a valid, writable, non-Family project document.
- Tripo output is treated as meters, Y-up, and right-handed, then converted to
  Revit's internal units and Z-up coordinate system.
- Revit design-coordinate limits are checked.
- Two import modes, both non-solid tessellated geometry:
  - `mesh` — one transaction creates one Generic Models `DirectShape`.
    `ApplicationDataId` binds the import UUID to a request fingerprint, so an
    identical retry does not create a duplicate; a `DirectShape` persists no
    material/texture counts, so its `already_exists` receipt reports zero for
    both.
  - `family` (what `importMode=native` resolves to) — a family template is
    probed (`TRIPO_REVIT_FAMILY_TEMPLATE` first, then the four exact candidate
    names listed above at the `FamilyTemplatePath` root and up to 32 immediate
    subdirectories); a new in-memory family document builds the geometry and
    materials in its own transaction and is saved to `<local data
    directory>/families/<idempotency key>.rfa`; the family is then loaded into
    the host document (or an already-loaded family with the same derived name
    is reused), Revit's first returned family symbol is selected, and one
    `FamilyInstance` is placed at project origin (`XYZ.Zero`) in a second
    transaction. A committed import returns the `.rfa` path as
    `savedFamilyPath`; an `already_exists` replay returns `null` there if that
    file has since been removed.
- `applyMaterials=true` creates a Revit material per OBJ material slot from its
  `Kd`/`d`/`Tr` color and alpha. A diffuse texture is bound into a duplicated
  appearance asset only when the target document already has an existing
  `AppearanceAssetElement` to duplicate from; a target document without one still
  yields a correctly colored but untextured material — an environmental limit,
  not an error. Texture validation fails closed with the typed errors described
  below.
- Document transactions use a failure preprocessor and roll back on error
  instead of waiting for an interactive failure dialog. A successfully saved
  `.rfa` may remain on disk in the `family_close_failed` case described below.
- `family`-mode idempotency uses an Extensible Storage schema entity on the
  created `FamilyInstance` that persists the idempotency key, request
  fingerprint, and the actual `MaterialCount`/`TextureCount`, so an
  `already_exists` receipt reports what the document holds rather than the
  incoming request's intent.

## Import receipt

The host receipt reports `createdId` (the `DirectShape` or `FamilyInstance`
unique ID), `transactionStatus` (`committed` or `already_exists`), the resolved
`importMode`, geometry counts, and material/texture counts. A `DirectShape`
`already_exists` receipt reports zero material and texture counts because those
counts are not persisted on that element. `savedFamilyPath` follows the
family-file rule above and is always `null` for mesh mode. This receipt is
mutation/idempotency evidence, not visual-rendering acceptance.

## Troubleshooting

### `host_unavailable`

Check that Revit 2026 is running, the manifest and DLL layout match exactly,
Revit was restarted, no bridge-startup dialog appeared, both processes use the
same Windows account, any `TRIPO_HOST_PID` is correct, and
`TRIPO_LOCAL_DATA_DIR` is either unset on both sides or identical on both
sides. Also replace the complete add-in and MCP outputs from the same revision
and restart both processes: a mixed host-control deployment is normally
ignored during discovery and appears as `host_unavailable`.

### `host_ambiguous`

More than one Revit bridge is live. Set `TRIPO_HOST_PID` to the intended Revit
PID and restart the MCP server.

### `document_unavailable` or `document_not_writable`

Open and activate a writable Revit project. Family documents, read-only
documents, and documents already inside another modification context are
rejected. Then call `tripo_host_context` again.

### `document_changed`

The active project no longer matches an import target session. Generation and
conversion are unaffected. Call `tripo_host_context` again before preparing a
new import UUID for the newly active project. If an import dispatch is
unresolved, return to its original target and keep the original import UUID,
conversion task and content, name, resolved mode, and materials flag while
reconciling it.

### `host_busy`

Wait for Revit to finish its current API work, then retry. For a UUID-bearing
creation or import stage, reuse the same UUID and identical arguments. Avoid
concurrent imports.

### API-key errors

Set the real key in the MCP server environment. Supply only the key characters:
do not add `Bearer`, whitespace, control characters, or literal quote
characters. JSON configuration still requires quotes around the string; those
delimiters are not part of the key. `tripo_host_context` and local
operation-status reads can work without a key; Tripo API tools cannot.

### The MCP process does not start

Confirm that a .NET 8 runtime is installed, the command and assembly paths are
absolute, the complete MCP output directory is present, and the client can
resolve the configured `dotnet` executable.

### `outcome_unknown`

The remote paid request may already have succeeded. Query
`tripo_operation_status`, preserve the journal, and inspect Tripo task/billing
history. Do not send another paid request automatically. A killed process can
leave a readable `dispatching` record; acquiring that same operation converts
it to `outcome_unknown` without resending.

### Family-mode errors

- `family_template_unavailable`: set a valid absolute
  `TRIPO_REVIT_FAMILY_TEMPLATE` before Revit starts, especially when the
  installation does not ship one of the four probed filenames.
- `family_close_failed`: the `.rfa` was saved, but Revit could not close the
  background family document; keep the saved file and inspect Revit before
  retrying.
- `family_load_failed` or `family_symbol_missing`: verify that the selected
  template can create and load a Generic Models family with at least one
  usable symbol.

### Material or bundle errors

Use `withMaterials=true` during OBJ conversion before importing with
`applyMaterials=true`. A texture entry referenced by the MTL but absent from
the bundle fails as `mtl_invalid`; a missing staged file fails as
`artifact_missing`; and a byte length or SHA-256 mismatch fails as
`artifact_hash_mismatch`. If the Revit target document has no duplicable
appearance asset, valid MTL colors are still applied but the bitmap is not
bound.

## Current limitations

- One non-solid Generic Models `DirectShape` (`mesh` mode) or one Family loaded
  and placed as one `FamilyInstance` at project origin (`family` mode), per
  import.
- The host window currently supports text-to-3D only. Local PNG/JPEG staging,
  upload, and image-to-3D creation are available through MCP; window image
  controls, WebP, and public-URL input remain open.
- Default text-generation model `v3.1-20260211`; `TRIPO_MODEL` can select
  another syntactically valid identifier, and changing it changes text-task
  paid-operation identity.
- Materials are baked diffuse only (OBJ `Kd`/`d`/`Tr` color/alpha plus one
  `map_Kd` texture per slot; bitmap binding additionally requires a duplicable
  appearance asset in the template/target document): no true PBR channels.
  Text generation disables quad output; OBJ conversion disables quad output
  and animation.
- Family mode places the first symbol returned by Revit. A custom template
  should contain one intended Type; deterministic multi-Type selection,
  family-parameter mapping, shared-coordinate support, and placement UI are
  not implemented.
- If exactly one already-loaded Family has the derived retry name, it is reused
  without revalidating its content or provenance before its first usable
  symbol is placed.
- No installer, signing, or automatic update. MCP remains staged (no one-call paid MCP tool).
- Production HTTP connections intentionally do not use system proxies.
- No completed real Revit 2026 runtime acceptance.

See [Architecture](./docs/ARCHITECTURE.md),
[Materials design](./docs/MATERIALS-DESIGN.md),
[Security](./docs/SECURITY.md), and
[Testing and evidence](./docs/TESTING.md) for the detailed trust and
acceptance boundaries.

Repository provenance and reference decisions are recorded in
[Migration](./docs/MIGRATION.md) and
[Blender reference](./docs/BLENDER-REFERENCE.md). Candidate packaging is
documented under [`packaging/`](./packaging/README.md).

## License

No distribution license has been selected. Until one is added, do not treat
this source as licensed for redistribution. The Blender reference informed
repository and product structure only; no upstream source code was copied.
