# Tripo-Revit

[English](./README.md) | 简体中文

Tripo-Revit 是面向 Autodesk Revit 2026 的独立社区适配器。AEC 用户可以在
modeless WPF window 内运行完整 text-to-model workflow；agentic client 可以通过
MCP 使用同一套 workflow。通过校验的 OBJ 输出（仅几何，或附带 baked-diffuse
材质与贴图）会导入精确的 active Revit project，目标可以是 Generic Models
`DirectShape`，也可以是生成、加载并放置的 Family instance。

它不是 Tripo 或 Autodesk 官方产品。

```text
Revit WPF window            MCP client
       ↕ host-control          ↕ stdio
       Tripo.Revit.Mcp sidecar / server ── Tripo v3 HTTPS API
                    ↕ authenticated protocol-v2 host bridge
                 Tripo.Revit add-in
                    ↕ bounded ExternalEvent FIFO + transactions
                 exact active Revit project document
```

sidecar 是唯一解析、存储或使用 Tripo API key 的进程。add-in 的 password dialog
只通过 authenticated local control channel 转发临时值并清空输入框；它不会把 key
写入 `.rvt`、`.rfa` 或 Revit settings。

> **当前状态：**add-in 以 Windows x64 上的 Revit 2026 为目标。仓库包含 portable
> host-control/workflow/MCP/process tests 与 package-compilation gates；Windows
> CI lane 已配置为编译 modeless WPF window、ribbon command、sidecar launcher、
> text workflow、credential dialog 与 bundled sidecar layout，并会在隔离 test
> target 下对 synthetic Credential 执行 write/read/delete。真实 Revit 加载、
> WPF/owner 行为、production-user Credential Manager interaction、
> transaction/Undo、尺度/方向、性能和视觉验收仍是未完成的 gates。目前没有安装器、
> 签名或自动更新机制。

## 前置要求

- Windows x64 与 Autodesk Revit 2026。
- 使用 .NET 8 SDK restore 和 build 项目。仓库选择 `8.0.100`，并允许在 .NET 8
  内按 `latestFeature` roll forward；restore 需要访问 NuGet。
- 运行 framework-dependent MCP server 需要 .NET 8 runtime；SDK 已包含该 runtime。
- 只有可选 MCP 路径需要支持 stdio server 的 MCP client。
- remote generation 与 conversion 需要 Tripo v3 API key。
- 一个可写、非 Family 的 Revit project，用于接收导入。
- Revit、panel sidecar 与任何 MCP server 必须以同一个 Windows user 运行。

宿主项目以 `net8.0-windows`、`x64` 为目标，并使用固定版本
`2026.0.0.9999` 的 `Autodesk.Revit.SDK` package 作为编译引用。仓库没有记录该
package 的已验证 hash 或 provenance receipt。生产 build 应验证该 dependency，或
改用受控的 Revit 2026 安装/SDK 引用。

## 构建

在仓库根目录通过 PowerShell 运行：

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

输出目录：

```text
src/Tripo.Revit/bin/Release/net8.0-windows/
src/Tripo.Revit.Mcp/bin/Release/net8.0/
```

非 Windows build 只能作为 compile-time 证据；加载和使用 add-in 需要 Windows 上的
Revit 2026。

构建 host project 时，也会构建 matching sidecar，并把完整输出复制到
`src/Tripo.Revit/bin/Release/net8.0-windows/sidecar/`。只有可选 MCP 路径才需要单独的
`src/Tripo.Revit.Mcp/bin/Release/net8.0/` 输出。Bridge protocol v2 与 host-control
protocol v3 都没有向后兼容 shim，因此所有组件必须来自同一个仓库 revision。

## 安装 Revit add-in

安装或替换文件前先关闭 Revit。

每用户部署布局是：

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
    └── 同一次 host build 的其他输出文件
```

manifest 必须直接放在 `2026` 目录下。它的相对 assembly entry 是：

```xml
<Assembly>TripoRevit\Tripo.Revit.dll</Assembly>
```

在仓库根目录运行以下 PowerShell，可填充该布局：

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

复制完整输出比猜测最小 dependency set 更安全。`RevitAPI.dll` 与
`RevitAPIUI.dll` 标记为 `Private=false`，由 Revit 提供，因此 build 不应部署它们的
本地副本。

该示例会覆盖同名文件，但不会删除旧 revision 遗留的文件。要干净升级，请保持 Revit
关闭，把现有 `TripoRevit` 目录移到一旁作为 rollback，再填充新的同名目录。

Autodesk 记录了每用户与 all-users 的 `.addin` 注册位置：
[Registration of add-ins](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_Registration_of_add_ins_html.html)。
本仓库的示例有意采用不需要安装器的 per-user 位置。

安装后重启 Revit。Bridge 会作为 `IExternalApplication` 自动启动，add-in 会创建
**Add-Ins → Tripo → Tripo 3D**。该按钮会打开或激活一个 modeless workflow
window。若启动失败，Revit 会显示 **Tripo MCP Bridge** dialog。

## 配置可选 MCP server

把以下目录的完整内容一起保存到稳定本地目录：

```text
src\Tripo.Revit.Mcp\bin\Release\net8.0\
```

相邻 assemblies、`.deps.json` 与 `.runtimeconfig.json` 都是必需的；不能只部署
`Tripo.Revit.Mcp.exe` 或 `.dll`。

server 通过 stdio 使用 MCP。它不是 HTTP service，也没有 port 配置。可移植的调用
方式是：

```powershell
dotnet C:\absolute\path\to\Tripo.Revit.Mcp.dll
```

也可以使用生成的 Windows apphost：

```powershell
C:\absolute\path\to\Tripo.Revit.Mcp.exe
```

通常由 MCP client 启动该进程。手动运行只会让它等待 stdio handshake。

下面是使用 `mcpServers`、`command`、`args` 与 `env` 的 client 常见配置结构。请按
实际 client schema 与 secret 机制调整：

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

使用绝对路径；Windows path 在 JSON 中需要转义反斜线。若 GUI MCP client 的
`PATH` 受限，使用 `dotnet` 的绝对路径。

### 环境变量

| 变量 | 设置位置 | 要求 |
| --- | --- | --- |
| `TRIPO_API_KEY` | sidecar / MCP server | 可选的环境 key；存在时覆盖 session key 与 stored key。window 不需要该变量也能设置 key。 |
| `TRIPO_MODEL` | sidecar / MCP server | 可选的 text-generation model identifier。默认是 `v3.1-20260211`；覆盖值必须匹配 `[A-Za-z0-9._-]{1,64}`，会由 text-task receipt 返回，并属于 text-task 付费请求 identity。window 路径需在 Revit 启动前设置。 |
| `TRIPO_HOST_PID` | 仅 MCP server | 同时存在多个 live Revit bridge 时必需，且必须是正整数。 |
| `TRIPO_LOCAL_DATA_DIR` | Revit 与 sidecar / MCP server | 可选的绝对、私有、稳定本地路径；所有参与进程必须解析到完全相同的值。 |
| `TRIPO_SIDECAR_PATH` | 仅 Revit process | 可选的 matching `Tripo.Revit.Mcp.dll` 或 native apphost 绝对路径，仅供开发覆盖。正常部署使用安装目录内复制的 `sidecar/`；需在 Revit 启动前设置。 |
| `TRIPO_REVIT_FAMILY_TEMPLATE` | 仅 Revit 进程 | `family` 模式可选的 `.rft` family template 绝对路径，必须在 Revit 启动前设置。未设置、不是绝对路径或文件不存在时，add-in 会继续在 Revit `FamilyTemplatePath` 根目录依次探测精确文件名 `Metric Generic Model.rft`、`Generic Model.rft`、`英制常规模型.rft`、`公制常规模型.rft`，再按 ordinal 顺序探测前 32 个直属子目录。其他 locale 应显式设置有效绝对路径；若均找不到则返回 `family_template_unavailable`。 |

window 路径请使用 **API key…**：保持 **Save in this user's OS credential
store** 勾选，会写入 Windows Credential Manager；取消勾选则只保存在 sidecar
process memory。UI 只报告 `environment`、`session`、`store` 或 `none`，绝不回显
key。MCP 路径最好使用 client credential store 或继承的进程环境。配置文件可能以
明文保存 `env` value；`${NAME}` 插值由 client 决定。本仓库不会加载 `.env` 文件。
替换 effective key 会改变 paid-operation identity，并可能让未完成 window 或 MCP
operation 的 same-UUID recovery fail closed；轮换 key 前必须先核对并解决每个未完成
的付费 UUID。

window 持久化 key 是 target 为 `TripoMCPs/TripoV3/<username>` 的 Windows Generic
Credential；不会写入 `.rvt`、`.rfa`、Revit settings 或临时文件。不要自行创建
temp key file：取消勾选可仅存 sidecar process memory，保持勾选使用当前用户 native
store，MCP 则使用 client secret/environment 机制。

最安全的设置是 Revit 与 MCP server 都不设置
`TRIPO_LOCAL_DATA_DIR`。双方会共同使用 `%LOCALAPPDATA%\TripoMCP`。

若自定义该目录：

- 在 Revit 启动前设置 Revit 进程环境，并在 MCP server 环境中设置完全相同的值；
- 使用稳定本地 filesystem 上的绝对、私有路径；
- 不要使用 NFS/SMB；
- 恢复期间不要移动或删除其中的 `bridges`、`controls`、`staging`、
  `image-transfers`、`operations`、`secrets`、`ui-recovery` 或 `families`。

只在 MCP client 中设置该变量会让 server 与 Revit 使用不同的 discovery/staging
roots，无法建立正确的 bridge 连接。

`image-transfers` 可能暂存由 MCP 选择的 PNG/JPEG 私有 snapshot，直到 file-token 或
upload-ambiguity checkpoint 持久化。恢复期间应与 paid-operation journal 一起保留。

## 使用 Revit window

1. 启动 Revit 2026，打开可写的目标 project，然后选择
   **Add-Ins → Tripo → Tripo 3D**。重复点击会激活同一个 modeless window。
   window 仍持有 workflow state 时，点击关闭按钮只会隐藏它，不会丢弃 session；
   再次使用 ribbon command 即可显示。
2. window 会自动连接精确 active project；也可以点击 **Connect / Refresh**
   显式刷新。当该 window 仍持有 workflow state 时，不同 document session 会被拒绝。
3. 若没有可用 key，在 **API key…** 粘贴 key，并选择 persistent 或 session-only
   storage。可在 [Tripo Platform](https://platform.tripo3d.ai/api-keys) 创建 key。
4. 输入 prompt、face limit 与材质选项，然后点击 **Generate**。window 会先显示
   可选择复制的 durable operation UUID，再显示 credit confirmation；拒绝确认不会
   发送付费请求。
5. 点击 **Refresh generation**，直到 task 为 `success`。
6. 点击 **Convert to OBJ**。该阶段使用另一个 UUID 与第二次独立费用确认；
   刷新 conversion 直到成功。
7. 选择 **Revit family（推荐）** 或 **DirectShape mesh**，填写 object name，
   并选择是否尝试应用 OBJ bundle 中的 baked materials，然后点击
   **Import into Revit**。最终 receipt 会报告实际创建的 element、material 与
   texture 数量；请求应用材质不等于声称 bundle 中确实存在材质。

modeless window 不直接访问 `Document` 或 `UIApplication`；context 与 mutation 仍
沿 sidecar → host bridge → bounded `ExternalEvent` 执行。它不自动轮询，也不声称能
取消远端 task。响应丢失后，该阶段必须先执行 **Refresh**；只有 paid-operation
journal 表明 creation 可以继续时，重试才会启用，button 会明确标为
**Retry … (same UUID)**。task 状态在手动刷新前会明确标为 last-known；只有
queued/running task 返回 0–100 范围内的真实 progress 时才显示 progress bar。已确认
的 import receipt 会禁用对应 action；异常 receipt 会继续可见、可复制，并保留同
UUID retry，不会被当作成功清除。当前 workflow 显式 reset 前，**New workflow**
会持续禁用。环境变量提供的 key 不能从 panel 覆盖；已被接受或结果不明确的
dispatch 会一直绑定原 Tripo account 直到 reset：替换 key 只能 session-only，必须
属于同一 account；没有 durable task 的未决 paid UUID 还必须恢复精确原 key。
durable `request_rejected` 不同：它证明没有创建远端 task。generation 被拒时会清除
generation 与全部 downstream stage；conversion 被拒时只清除 conversion/import，
保留成功且 account-bound 的 generation。修正 credential 后，为被拒阶段准备新 UUID。

dispatch 前，window 的 shared state layer 会在
`%LOCALAPPDATA%\TripoMCP\ui-recovery\revit\<recovery-id>.json`
（或 `TRIPO_LOCAL_DATA_DIR` 的对应 root）原子写入 private recovery hint。hint 只
包含 UUID、作为 conversion source 的 durable generation task、已知的结果 task ID
与 import retry 所需的最少参数；不包含 prompt、API key、Authorization header、URL
或任意 path。

这个具有独立 identity 的 hint 会在 import 成功后继续保留，直到 live workflow 被
显式 reset。Revit 退出或崩溃后，下次打开 window 会显示 stale recovery ID，并阻止
新 workflow。
另一个 Revit process 拥有的 hint 会保守地阻塞，因为不能跨 process 猜测 panel session
是否仍存活；recovery 必须在 owner process 中完成，或在确认它退出后再进行。
这个 Revit build 在观察到 Rhino/Revit 的任意 UI paid hint、未确认 import、无法确认
已退出的 foreign-owner record 或无效 recovery storage 时，也会拒绝 API-key change。
“由 Rhino 发起的 key change”只有在独立 build/deploy 的 Rhino panel 同样带有该
保守 filter 时才受对称保护。配套 source revision 现在已包含它，但 legacy Rhino
binary 仍可绕过该方向；兼容的双宿主部署仍是 cross-host release dependency。一个
root-global UI intent lease 会串行化跨 panel 的 credential-recovery scan、
key-mutation request 与 paid dispatch call。另一个 private sidecar execution lease 会持有实际 key
mutation，以及每个 UI 或 standalone MCP paid workflow 从 credential-derived
fingerprint 直到 durable task、明确 `request_rejected` 或 ambiguous-outcome journal
checkpoint 的全过程；即使 UI pipe 断开也不会提前释放。同一时间只允许一个 key
mutation 或 paid create/convert；发生竞争时，必须等当前 operation checkpoint 后，
以同一 UUID 重试。**Inspect recovered IDs** 只查询本地
`operation_status`，不会重发付费 call 或 import。import 必须使用显示的同一 UUID
人工 reconciliation。核对全部 ID 后，**Acknowledge reviewed IDs…** 要求勾选适用的
paid/import review 项。inspection 会签发与当前 recovery snapshot 绑定的一次性
receipt；归档前还会重新载入 recovery set，并重新核对所有先前成功的 paid-journal
status。只要任一项发生变化，最终操作就会被拒绝，刷新后的 snapshot 必须重新
inspection。inspection 失败会明确显示 **Manual review required**，并要求额外勾选
failure review。无效、超限、未知 schema 或 symlink hint 会继续阻塞，等待人工检查；
自定义 Windows data root 必须保持 current-user-only ACL。paid-operation journal
才是 authority，hint 不是。Revit window 仍然只支持 text-to-3D；本地图生功能目前由
可选 MCP tools 提供。

## 启动并验证 MCP

可选 MCP 路径：

1. 在 Revit 关闭时安装 add-in。
2. 启动 Revit 2026。
3. 打开要接收模型的可写 project。
4. 启动或重启 MCP client，让 server 收到预期环境。
5. 确认 client 列出下文八个 tools。
6. 调用 `tripo_host_context`。

成功的 context receipt 证明 MCP server 已经连接到 Revit，并返回：

- `host: "revit"`；
- 实际 Revit version 与 process ID；
- active document title 与 units；
- 精确的临时 `documentSessionId`；
- 支持的 host capabilities。

后续 tools 必须原样保留该 `documentSessionId`。若 active document 改变，应重新
调用 `tripo_host_context`，不能静默继续操作另一个 project。

项目没有 HTTP health endpoint。若同时运行多个 Revit process，discovery 会返回
`host_ambiguous`，不会猜测目标。把 `TRIPO_HOST_PID` 设为目标 Revit PID，然后重启
MCP server。

## MCP 工具

MCP 入口通过以下八个 tools 暴露同一套 shared workflow：

| 工具 | 主要参数 | 作用 |
| --- | --- | --- |
| `tripo_host_context` | 无 | 读取已连接 Revit 进程与精确 active-document session；不调用 Tripo API。 |
| `tripo_task_status` | `taskId` | 查询一个已有 Tripo task。 |
| `tripo_operation_status` | `operationId` | 读取一条持久化的本地 paid-operation record；不调用 Tripo 或 Revit。 |
| `tripo_create_text_task` | `prompt`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 创建一个 text-to-model task。`withMaterials=true` 请求 textured PBR generation（`texture`/`pbr`）；`false` 保持 geometry-only。可能消耗 credits。 |
| `tripo_stage_local_image` | `localImagePath` | 校验并私有 snapshot 一张本地 PNG/JPEG，返回 opaque descriptor；不调用 Tripo。 |
| `tripo_create_image_task` | `transferId`、`sha256`、`byteLength`、`mediaType`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 上传一张 staged image，并以独立 upload/generation durable checkpoints 创建 image-to-model task。四个 descriptor fields 必须原样复制自 `tripo_stage_local_image`。可能消耗 credits。 |
| `tripo_create_obj_conversion` | `sourceTaskId`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 创建一个 OBJ conversion。`withMaterials=true` 请求带 baked-diffuse MTL 与 image textures 的 OBJ bundle（`bake=true`）；`false` 只转换几何。可能消耗 credits。 |
| `tripo_import_obj_task` | `conversionTaskId`、`name`、`documentSessionId`、`operationId`、`importMode`（默认 `native`）、`applyMaterials`（默认 `false`） | 下载、校验并将成功的 OBJ conversion 导入为 `DirectShape` 或 Family instance。 |

输入边界：

- `prompt`：1–1024 个字符；
- `faceLimit`：500–200000；
- 导入 `name`：1–128 个字符；
- task ID 必须以 `task_` 开头；
- `documentSessionId` 必须是 `tripo_host_context` 返回的精确 UUID；
- 每个 `operationId` 都是 caller-generated UUID；
- `importMode` 可以是 `native`、`mesh` 或 `family`；本 build 会以
  `import_mode_unsupported` 拒绝 `instance`，而 `native` 解析为 `family`；
- `applyMaterials=true` 时，若 converted bundle 没有 MTL 会 fail closed。

只有在用户明确接受可能的外部费用后，`confirmExternalCost=true` 才有效。

## 典型工作流

1. 调用 `tripo_host_context` 并保留其精确 `documentSessionId`。
2. 选择一种 generation 分支：
   - text：生成 UUID A；在用户明确确认费用后调用 `tripo_create_text_task`；
   - 本地图像：先调用 `tripo_stage_local_image`，再生成 UUID A；明确确认费用后，
     把 descriptor 返回的 `transferId`、`sha256`、`byteLength` 与 `mediaType`
     分别传入四个同名参数，再调用 `tripo_create_image_task`。
3. 用 `tripo_task_status` 轮询返回的 task ID，直到 `success` 或 terminal failure。
   遇到 `failed`、`cancelled`、`banned` 或 `expired` 时停止。
4. 生成 UUID B；再次取得明确费用确认后调用
   `tripo_create_obj_conversion`。
5. 轮询 conversion task，直到 `success` 或 terminal failure。
6. 生成 UUID C，调用 `tripo_import_obj_task`，并按需选择
   `importMode` 与 `applyMaterials`。
7. 检查 host receipt 与创建出的 `DirectShape` 或 Family instance。

若要导入材质，两个付费创建阶段都使用 `withMaterials=true`，导入阶段使用
`applyMaterials=true`；geometry-only 工作流则把三个 flags 都保持为 `false`。

两个付费 creation calls 与 host import 必须使用三个不同的 caller-owned UUID。
已提交的 import 通常返回 `transactionStatus: "committed"`；相同重试可能返回
`"already_exists"`。

付费阶段响应丢失时，先使用 `tripo_operation_status` 检查对应的本地记录。只有
journal 表明 creation 可以继续时，才以原 UUID、完全相同的显式参数、API key 与
document session 重试；text-task 重试还必须保持相同的 effective model。

若 operation 为 `outcome_unknown`，不要自动重发或创建替代 UUID。保留 journal，并
人工核对 Tripo task 或 billing history。

若 operation 为 `request_rejected`，provider 已明确拒绝请求且没有创建 task。修正
credential 并准备新 UUID；不要重试被拒 UUID。

Image creation 会分别 checkpoint upload 与 generation。持久化的 `file_token` 允许
继续 generation 而不再次 upload。若 upload 或 generation 结果不明确，会记录具体
stage 并拒绝自动重发；人工核对前需保留 `image-transfers/` 与 journal。

导入恢复有意采用不同规则。复用 import UUID、conversion task 与 artifact
content、name、解析后的 mode 和 materials flag。Revit 重启后，应重新打开同一个目标
project，调用
`tripo_host_context`，并传入新的 `documentSessionId`；host fingerprint 排除了该
临时 session ID，但 active-session check 仍会 fail closed。要让
`already_exists` 跨应用重启生效，原 import 后必须保存 Revit project；若未保存的改动
已经丢失，持久化项目中没有可回放 element，重试会再次 commit 该 import。

## Revit 导入行为

- 所有 Revit API 读取与写入都通过 `ExternalEvent` 执行。
- Host requests 使用 bounded FIFO；queue 满时返回 `host_busy`。
- active document 会在 staging 前检查，并在 mutation 前再次检查。
- Converted OBJ、可选 MTL 与 PNG/JPEG textures 构成 content-addressed bundle。
  每个保留 entry 都按 manifest 校验；bundle 最多保留 32 个 entries，每个最多
  128 MiB，总计最多 256 MiB。
- import 要求有效、可写、非 Family 的 project document。
- Tripo output 视为 meters、Y-up、right-handed，再转换为 Revit internal units 与
  Z-up coordinate system。
- 会检查 Revit design-coordinate limits。
- 两种导入模式，几何均为 non-solid tessellated：
  - `mesh`：一次 transaction 创建一个 Generic Models `DirectShape`。
    `ApplicationDataId` 把 import UUID 绑定到 request fingerprint，因此相同 retry
    不会创建副本。`DirectShape` 不持久化 material/texture counts，所以其
    `already_exists` receipt 对两者都报告 0。
  - `family`（`importMode=native` 的解析结果）：先探测 family template（首先是
    `TRIPO_REVIT_FAMILY_TEMPLATE`，然后是上文列出的四个精确候选文件名及最多 32 个
    直属子目录）；在新的 in-memory family document 自身 transaction 中创建几何与
    材质，保存到 `<local data directory>/families/<idempotency key>.rfa`；随后把
    family 加载到 host document（或复用同 derived name 的已加载 family），选择
    Revit 返回的第一个 family symbol，并在第二个 transaction 中把一个
    `FamilyInstance` 放在 project origin（`XYZ.Zero`）。新提交的 import 通过
    `savedFamilyPath` 返回 `.rfa` 路径；`already_exists` replay 时，如果文件已被
    删除，该字段会返回 `null`。
- `applyMaterials=true` 会按每个 OBJ material slot 的 `Kd`/`d`/`Tr` color 与
  alpha 创建 Revit material。只有 target document 已存在可复制的
  `AppearanceAssetElement` 时，才会把 diffuse texture 绑定到复制出的 appearance
  asset；target document 没有该资产时，仍得到颜色正确但无贴图的 material，这是
  环境限制而不是错误。Texture validation 会以文末列出的 typed errors fail closed。
- Document transaction 使用 failure preprocessor，并在错误时 rollback，而不是等待
  交互式 failure dialog；发生下文 `family_close_failed` 时，已成功保存的 `.rfa`
  可能仍留在磁盘。
- `family` 模式在创建的 `FamilyInstance` 上使用 Extensible Storage schema entity
  保存 idempotency key、request fingerprint 与真实的
  `MaterialCount`/`TextureCount`；因此 `already_exists` receipt 反映文档实际保存的
  状态，而不是 incoming request 的意图。

## 导入回执

宿主 receipt 会报告 `createdId`（`DirectShape` 或 `FamilyInstance` unique ID）、
`transactionStatus`（`committed` 或 `already_exists`）、解析后的 `importMode`、
几何计数与 material/texture counts。由于这些 counts 没有持久化在
`DirectShape` 上，其 `already_exists` receipt 对两者都报告 0。
`savedFamilyPath` 遵循上文 family-file 规则，在 mesh 模式始终为 `null`。这些字段是
mutation/idempotency 路径的证据，不是视觉渲染验收。

## 故障排查

### `host_unavailable`

检查 Revit 2026 是否正在运行、manifest 与 DLL layout 是否完全匹配、是否已重启
Revit、是否出现 bridge-startup dialog、两个进程是否使用同一 Windows account、
`TRIPO_HOST_PID` 是否正确，以及 `TRIPO_LOCAL_DATA_DIR` 是否在两端都未设置或完全
相同。还应从同一个 revision 替换完整 add-in 与 MCP 输出并重启两个进程；
host-control 版本混合部署通常会被 discovery 忽略，并表现为
`host_unavailable`。

### `host_ambiguous`

有多个 Revit bridge 存活。把 `TRIPO_HOST_PID` 设为目标 Revit PID，然后重启 MCP
server。

### `document_unavailable` 或 `document_not_writable`

打开并激活可写的 Revit project。Family document、read-only document，以及已处于
另一个 modification context 的 document 都会被拒绝。然后重新调用
`tripo_host_context`。

### `document_changed`

active project 已不再匹配保留的 session。重新调用 `tripo_host_context`。
paid-operation identity 不会迁移到新 session。已发送或响应丢失的付费阶段应先调用
`tripo_operation_status`。除非 journal 报告明确 `request_rejected`，否则保留原 UUID
与 identity；若被明确拒绝，应修正 credential 并准备新 UUID。只有
重新打开同一个目标 project，并保持原 import UUID、conversion task 与 content、
name、解析后的 mode 和 materials flag 时，import retry 才能使用新 session。

### `host_busy`

等待 Revit 完成当前 API 工作后重试。对带 UUID 的 creation 或 import 阶段，复用
同一个 UUID 与相同参数。避免并发 imports。

### API key 错误

在 MCP server 环境中设置真实 key。只提供 key 本身：不要添加 `Bearer`、空白、
control characters 或字面 quote characters。JSON 配置仍需要用双引号包围字符串，
但这些 delimiters 不属于 key。`tripo_host_context` 与本地 operation-status 读取
不需要 key；Tripo API tools 需要。

### MCP 进程无法启动

确认已经安装 .NET 8 runtime，command 与 assembly path 都是绝对路径，完整 MCP 输出
目录仍在，并且 client 能解析配置的 `dotnet` executable。

### `outcome_unknown`

远端付费请求可能已经成功。查询 `tripo_operation_status`、保留 journal，并检查
Tripo task/billing history；不要自动发送另一个付费请求。进程被终止后可能留下可读的
`dispatching` record；acquire 同一个 operation 会把它改写为
`outcome_unknown`，但不会重发。

### Family 模式错误

- `family_template_unavailable`：尤其当安装环境没有提供四个已探测文件名之一时，应在
  Revit 启动前设置有效的 `TRIPO_REVIT_FAMILY_TEMPLATE` 绝对路径。
- `family_close_failed`：`.rfa` 已保存，但 Revit 无法关闭后台 family document；
  保留该文件，并在重试前检查 Revit。
- `family_load_failed` 或 `family_symbol_missing`：确认所选模板能创建并加载一个至少
  含可用 symbol 的 Generic Models family。

### 材质或 bundle 错误

在以 `applyMaterials=true` 导入前，OBJ conversion 必须使用
`withMaterials=true`。MTL 引用的 texture entry 不在 bundle 时返回
`mtl_invalid`；staged file 缺失时返回 `artifact_missing`；字节数或 SHA-256
不匹配时返回 `artifact_hash_mismatch`。若 Revit target document 没有可复制的
appearance asset，有效 MTL color 仍会应用，但 bitmap 不会绑定。

## 当前限制

- 每次 import 创建一个 non-solid Generic Models `DirectShape`（`mesh`），或加载
  一个 Family 并在 project origin 放置一个 `FamilyInstance`（`family`）。
- 当前宿主 window 只支持 text-to-3D。本地 PNG/JPEG staging、upload 与
  image-to-3D creation 已由 MCP 提供；window image controls、WebP 与 public URL
  input 尚未实现。
- text-generation 默认 model 是 `v3.1-20260211`；`TRIPO_MODEL` 可以选择另一个
  通过语法校验的 identifier，改变它也会改变 text-task paid-operation identity。
- 材质只支持 baked diffuse（OBJ `Kd`/`d`/`Tr` color/alpha 加每个 slot 一张
  `map_Kd` texture；bitmap binding 还要求 template/target document 具有可复制的
  appearance asset）：没有真正的 PBR channels。Text generation 关闭 quad
  output；OBJ conversion 关闭 quad output 与 animation。
- Family 模式放置 Revit 返回的第一个 symbol。自定义 template 应只包含一个预期
  Type；尚未实现确定性的 multi-Type 选择、family-parameter mapping、
  shared-coordinate support 或 placement UI。
- 若恰好存在一个与 derived retry name 相同的已加载 Family，系统会直接复用它，并在
  放置第一个可用 symbol 前不再验证其 content 或 provenance。
- 没有 one-call paid workflow、安装器、签名或自动更新。
- Production HTTP connections 有意不使用 system proxies。
- 尚未完成真实 Revit 2026 runtime acceptance。

详细信任与验收边界见 [Architecture](./docs/ARCHITECTURE.md)、
[Materials design](./docs/MATERIALS-DESIGN.md)、
[Security](./docs/SECURITY.md) 和
[Testing and evidence](./docs/TESTING.md)。

仓库来源与参考项目取舍见 [Migration](./docs/MIGRATION.md) 和
[Blender reference](./docs/BLENDER-REFERENCE.md)；候选包流程见
[`packaging/`](./packaging/README.md)。

## 许可证

尚未选择发布许可证。在许可证落定前，请不要把当前源码视为已授权再分发。Blender
参考项目只用于学习仓库与产品结构，没有复制其源码。
