# GARbro 机器友好 CLI 与 AI SKILL 长期方案

## Context

GARbro 已经包含两个命令行入口：

- `Console/` 生成 `Onachi-GARbro.Console.exe`，支持列出归档格式、浏览归档、
  按条目或整包提取。
- `Image.Convert/` 生成 `Onachi-GARbro.Image.Convert.exe`，支持图片识别、
  元数据查看与格式转换。

这两个工具最初定位为测试 playground。它们输出面向人类的文本，错误多数只写
入标准错误流，没有稳定退出码、版本化 JSON 协议、统一的非交互参数、安全限额
或结构化的“需要密钥/方案”响应。GUI 中已经具备更完整的脚本
`filtered`、`raw`、`dump`、`jsonl` 提取路径，但不能作为无界面自动化接口使用。

长期目标是新增一个独立、稳定、面向机器的 `GARbro.Cli` 应用，并在其上构建
仓库内的 `garbro-cli` Codex SKILL。CLI 负责所有确定性识别、读取、转换、安全
检查和结构化输出；SKILL 只负责理解用户意图、选择命令、应用安全工作流和解释
结果。

相关现状与约束：

- 解决方案是旧式 Visual Studio C# 项目，主要目标为 .NET Framework 4.8.1。
- 格式由 `GameRes.FormatCatalog` 通过 MEF 从 `Arc*.dll` 发现。
- 部分格式通过 `ParametersRequest` 请求游戏方案、密钥或其他参数，现有实现
  常由 WPF 控件提供输入。
- 脚本结构化输出已有 `ScriptTextEntry` 和 `ScriptJsonLines`，应直接复用。
- `Console/` 与 `Image.Convert/` 仍承担回归冒烟职责，第一阶段不能删除。
- 首个受支持平台是 Windows；本计划不隐含跨平台迁移承诺。

参考：

- `docs/architecture/project-structure.md`
- `docs/reference/build-and-verify.md`
- `docs/reference/script-text-extraction.md`
- `Console/ConsoleBrowser.cs`
- `Image.Convert/Program.cs`
- `GUI/GarExtract.cs`

## Goals

1. 提供稳定、可版本化、可被 AI 和脚本可靠调用的 GARbro 命令行协议。
2. 覆盖格式枚举、探测、归档浏览与提取、脚本结构化提取、图片信息与转换。
3. 所有机器调用都可以完全非交互运行，并明确表达缺少参数或部分成功。
4. 把归档路径逃逸、意外覆盖、解包规模失控和不完整输出作为一等安全问题。
5. 建立一个简洁的 repo-local SKILL，使 AI 不需要重新理解 GARbro 内部结构。
6. 保留现有 GUI、Console 和 Image.Convert 的行为，逐步迁移而非一次重写。

## Non-Goals

- 不在首期把 GARbro 改写为跨平台或现代 .NET 应用。
- 不让 SKILL 直接调用 GUI、模拟点击或解析面向人类的控制台文本。
- 不在 CLI 中嵌入模型、Agent runtime 或网络服务。
- 不在首期支持归档创建、批量覆盖、递归打开无限层嵌套归档等高风险写操作。
- 不要求所有历史加密格式立即提供通用无界面参数绑定。
- 不把 MCP 服务器作为首期依赖；稳定 CLI 协议是未来 MCP 包装层的基础。

## Target Architecture

### Project boundary

新增旧式项目：

```text
Cli/
  GARbro.Cli.csproj
  App.config
  Program.cs
  CommandLine.cs
  ExitCode.cs
  Output/
    JsonOutputWriter.cs
    MachineEnvelope.cs
  Commands/
    CapabilitiesCommand.cs
    FormatsCommand.cs
    ProbeCommand.cs
    ArchiveListCommand.cs
    ArchiveExtractCommand.cs
    ScriptExtractCommand.cs
    ImageInfoCommand.cs
    ImageConvertCommand.cs
  Parameters/
    NonInteractiveParameterBroker.cs
  Safety/
    ExtractionPolicy.cs
    OutputPathResolver.cs
    CountingOutputStream.cs
```

项目名为 `GARbro.Cli`，输出为 `Onachi-GARbro.Cli.exe`，目标
.NET Framework 4.8.1。它引用 `GameRes`，运行时继续从共享
`bin/<Configuration>/` 目录发现 `ArcFormats`、`Legacy` 和
`Experimental`。

CLI 项目可以直接依赖仓库已锁定的 Newtonsoft.Json 13.0.4。不要为了参数解析
引入大型框架；首期命令语法固定且有限，应采用小型、经过测试的本地解析器。

不要让 `GameRes` 依赖 CLI。只有当 GUI 和 CLI 确实共享同一段无界面业务逻辑时，
才把最小抽象下沉到 `GameRes`。WPF 对话框、控件和文件选择器必须留在 `GUI`。

### Responsibility boundary

```text
Codex / automation
  -> garbro-cli SKILL
    -> Onachi-GARbro.Cli.exe --output json|jsonl --non-interactive
      -> GameRes contracts and VFS
        -> MEF-discovered ArcFormats / Legacy / Experimental handlers
```

- CLI：参数验证、资源识别、格式调用、输出协议、退出码、路径与限额安全。
- 格式实现：格式识别、解密、解压、解码、脚本文本结构。
- SKILL：意图到命令的映射、先探测后写入、用户授权边界、结果摘要。
- 可选未来 MCP：只包装同一 CLI/application service，不复制格式逻辑。

## Command Surface

### Milestone 1: read-only foundation

```powershell
Onachi-GARbro.Cli.exe capabilities --output json
Onachi-GARbro.Cli.exe formats list --kind all --output json
Onachi-GARbro.Cli.exe probe <path> --output json
Onachi-GARbro.Cli.exe archive list <archive> --output jsonl
```

`probe` 不创建文件。它报告实际尝试结果、选中的 handler、资源类型、tag、
extension/signature 匹配信息和是否需要额外参数。GARbro 当前没有可靠的通用
“置信度”，因此协议不能伪造数值 confidence。

### Milestone 2: bounded extraction and conversion

```powershell
Onachi-GARbro.Cli.exe archive extract <archive> --destination <dir> `
  --entry <name-or-glob> --overwrite never --output jsonl --non-interactive

Onachi-GARbro.Cli.exe script extract <script-or-entry> --mode jsonl `
  --destination <dir> --output json --non-interactive

Onachi-GARbro.Cli.exe image info <image> --output json
Onachi-GARbro.Cli.exe image convert <image> --format png `
  --destination <dir> --overwrite never --output json
```

归档中的脚本条目可以先通过显式的 `--entry` 处理。统一的
`archive#entry` URI 或递归 VFS 地址只在路径转义和歧义规则稳定后加入。

### Later milestones

- 音频信息与转换。
- 批处理清单输入和可恢复任务。
- 有明确 schema 的格式参数绑定。
- 归档创建等写操作；必须单独设计确认、覆盖和可恢复策略。
- 基于同一 application service 的本地 MCP server；CLI 继续作为最小公共接口。

## Machine Protocol

### Versioning

每个 JSON 对象包含：

```json
{
  "schemaVersion": "garbro.cli/v1",
  "command": "archive.list",
  "status": "success",
  "data": {}
}
```

- v1 内允许增加可选字段。
- 删除、重命名或改变现有字段语义必须发布新的 major schema。
- CLI 程序版本和协议版本分别报告，不用程序集修订号代替协议版本。
- `capabilities` 返回命令、协议版本、可用格式种类和安全能力，供 SKILL
  在调用前做兼容判断。

### Output modes

- `--output text`：面向人类，允许格式化表格。
- `--output json`：单个最终 envelope，适合小结果。
- `--output jsonl`：一行一个事件，适合大目录、进度和批处理。

机器模式下：

- 标准输出只包含协议对象。
- 诊断日志写标准错误流。
- 默认不输出堆栈；`--verbose` 才允许详细诊断。
- JSONL 的最后一行必须是 `summary` 或 `error` 事件。
- 每次调用生成 `operationId`，所有事件都携带同一个 ID。

推荐 JSONL 事件种类：

```text
start
candidate
entry
progress
warning
result
summary
error
needs_input
```

### Status and exit codes

| Exit code | Status | Meaning |
| ---: | --- | --- |
| 0 | `success` | 命令完整成功 |
| 2 | `usage_error` | 命令或参数语法错误 |
| 3 | `invalid_input` | 输入路径、选项或数据无效 |
| 4 | `unrecognized` | 没有格式 handler 接受输入 |
| 5 | `needs_input` | 需要方案、密钥或其他未提供参数 |
| 6 | `conflict` | 文件存在、名称碰撞或策略拒绝 |
| 7 | `partial_success` | 部分条目完成，部分失败或跳过 |
| 8 | `io_error` | 可归类的文件系统或流错误 |
| 9 | `internal_error` | 未预期异常或协议不变量失败 |

不要把普通错误转换为退出码 0。批处理中的逐项失败写事件，最终根据汇总决定
0、7 或其他错误码。

## Non-Interactive Parameters

CLI 订阅 `FormatCatalog.ParametersRequest`。在 `--non-interactive` 下禁止弹窗或
读取控制台输入：

1. 尝试应用显式提供的 `--scheme`、`--options-file` 或受支持的格式参数。
2. 如果无法构造正确的强类型 `ResourceOptions`，立即返回 `needs_input`。
3. 返回 resource tag、notice、source path、已知参数 schema 和可接受的后续输入，
   不猜测游戏方案或密钥。

长期引入一个可选、无 GUI 依赖的参数描述接口，例如：

```csharp
public interface IHeadlessResourceOptionsProvider
{
    ResourceOptionSchema DescribeOptions (ResourceParameterContext context);
    ResourceOptions BindOptions (
        ResourceParameterContext context,
        IDictionary<string, object> values);
}
```

只有有真实 CLI 用例的格式才逐步实现该接口。未实现接口的历史格式仍然可以返回
`needs_input`，不应阻塞基础 CLI 发布。

秘密或长密钥优先通过受权限保护的 `--options-file` 或标准输入传入；SKILL
不得默认把秘密放到进程参数或日志中。所有错误响应必须脱敏。

## Extraction Safety

以下规则是 v1 的发布门槛：

1. 默认 `--overwrite never`；覆盖必须显式请求。
2. 所有输出条目先规范化，再确认最终绝对路径位于 destination 内。
3. 拒绝 rooted path、盘符、UNC、`..` 逃逸、空名称和非法 Windows 路径。
4. 对规范化后同名条目报告冲突，不静默覆盖。
5. 使用同目录临时文件写入，成功后原子移动；失败时清理 `.partial` 文件。
6. 支持并默认启用合理的：
   - `--max-files`
   - `--max-total-bytes`
   - `--max-entry-bytes`
   - `--max-depth`
7. 使用计数输出流限制实际解压字节，而不只相信归档索引中的声明大小。
8. 支持 `--dry-run`，返回将写入的 manifest 和预期冲突。
9. 捕获 Ctrl+C，停止调度新条目并尽力清理当前临时输出。
10. SKILL 不得使用未来可能出现的 `--allow-unsafe-*` 逃生参数。

读取和写入必须尽量流式执行。协议允许列表和进度使用 JSONL，不能因为要构造
一个 JSON 数组而把大型归档目录或全部输出内容一次性留在内存中。

## SKILL Design

实现阶段使用 `skill-creator` 初始化仓库内：

```text
.codex/skills/garbro-cli/
  SKILL.md
  agents/openai.yaml
  references/
    command-reference.md
    script-text-modes.md
    machine-protocol.md
    extraction-safety.md
```

只有发现重复且易错的辅助逻辑时才增加 `scripts/`。不要在 SKILL 内复制解包、
转换或 JSON 解析实现；确定性逻辑属于 CLI。

`SKILL.md` 保持简短，负责能力发现、CLI 定位、协议协商和按任务路由：

1. 定位 Release 或 Debug CLI；不存在时使用 `$garbro-build-verify` 构建。
2. 调用 `capabilities` 并验证 `garbro.cli/v1`。
3. 脚本导出前加载 `script-text-modes.md`，明确 `filtered`、`raw`、`dump`、
   生成文件 JSONL 和 stdout JSONL 的边界。
4. 归档写入前加载 `extraction-safety.md`，先列目录或 dry-run。
5. 需要语法时加载 `command-reference.md`；解析 envelope 或错误时加载
   `machine-protocol.md`。
6. 默认非交互、禁止覆盖、使用明确 destination 和规模限制。
7. 遇到 `needs_input` 时向用户说明缺少的具体方案或参数。
8. 解析最终 envelope/summary，报告输出位置、成功数、失败数和限制。
9. 不在没有用户授权的情况下扩大为整目录批量提取或覆盖。

产品级协议的耐久事实源仍是 `docs/reference/cli-machine-interface.md` 和
`docs/reference/script-text-extraction.md`。分发 ZIP 中的 SKILL references 是
面向代理运行时的自包含操作手册；构建测试逐文件比较 repo-local SKILL 与 ZIP，
并用关键词断言保护最容易漂移的模式、协议和安全边界。

SKILL 的触发描述应覆盖：

- 识别、浏览或提取视觉小说归档。
- 检查或转换 GARbro 支持的图片/音频。
- 将游戏脚本提取为 filtered/raw/dump/JSONL。
- 诊断 GARbro CLI 的格式识别、参数或提取失败。

创建后必须运行 `quick_validate.py`，并用未知归档、普通归档、路径冲突、
`needs_input`、脚本 JSONL 和部分失败等场景做 forward test。

## Compatibility and Migration

- 首个正式 CLI 发布后，`Console/` 和 `Image.Convert/` 至少保留两个发布周期。
- 新 CLI 的初期实现可以复用其已验证逻辑，但不能依赖解析这两个程序的文本输出。
- 先把 CI/本地 smoke 增加到新 CLI，再考虑把旧工具标记为 deprecated。
- 删除旧工具前必须确认 README、build 脚本、NSIS、repo skills 和所有 smoke
  命令都已迁移。
- GUI 继续使用现有交互流程；共享逻辑下沉时必须同时回归 GUI。

## Observability

- 每次调用输出 `operationId`、程序版本、协议版本和选中的 handler tag。
- `--verbose` 日志包含候选 handler、参数请求、跳过原因和异常链，但不包含密钥。
- 提取 summary 至少包含：
  `selected`、`written`、`skipped`、`failed`、`bytesWritten`、
  `durationMs`、`destination`。
- 对自动化有意义的 warning 使用稳定 code，而不是要求 AI 匹配本地化文本。
- 人类文本可以本地化；机器字段名、status、event type 和 warning/error code
  永远使用稳定英文标识符。

## Acceptance Criteria

- [x] `GARbro.sln` 包含 `GARbro.Cli`，Debug 和 Release 均可由 Visual Studio
      MSBuild 构建。
- [x] `capabilities`、`formats list`、`probe`、`archive list` 在机器模式下只输出
      可解析的 v1 JSON/JSONL。
- [x] 归档提取默认不覆盖，并通过真实写入计数和规范化路径阻止逃逸及规模失控。
- [x] 脚本命令复用 `IConfigurableScriptFormat`，诚实暴露各格式支持的文本模式。
- [x] 图片信息与转换不再要求 AI 解析 `Image.Convert` 的人类文本。
- [x] 所有需要 GUI 参数但尚无 headless provider 的格式返回 exit 5 和
      `needs_input` envelope。
- [x] Debug/Release CLI smoke、协议 golden files、路径安全和部分成功用例通过。
- [ ] 至少使用一个真实普通归档、一个需要参数的归档、一个脚本和一个图片样本
      完成端到端验证；不能公开的样本以本地路径记录测试结果，不提交资源本体。
- [x] NSIS 和 release build 包含 `Onachi-GARbro.Cli.exe` 及运行所需依赖。
- [x] 完整安装包包含可独立保存的 `garbro-cli-skill.zip`；设置页把 ZIP 复制到
      用户选择的位置，不直接修改 Codex skills 目录。
- [x] `docs/reference/cli-machine-interface.md`、README、项目结构和构建文档同步。
- [ ] `.codex/skills/garbro-cli` 通过 skill validator 和代表性 forward tests。
- [x] 旧 Console/Image.Convert 在兼容期内继续通过既有 smoke。

## Implementation Checklist

### Phase 0: contract and fixtures

- [x] 编写 `docs/reference/cli-machine-interface.md`，冻结 v1 envelope、事件和退出码。
- [x] 确定可合法提交的最小合成 fixtures；为私有样本建立不入库的本地测试清单。
- [x] 记录当前 Console/Image.Convert 输出作为迁移基线。
- [x] 在 `GARbro.sln` 中添加 `Cli/GARbro.Cli.csproj`。

### Phase 1: read-only MVP

- [x] 实现命令解析、全局参数、输出 writer 和统一异常映射。
- [x] 实现 `capabilities`、`formats list` 和 `probe`。
- [x] 实现流式 `archive list` JSONL。
- [x] 实现 `NonInteractiveParameterBroker` 和 `needs_input`。
- [x] 添加 PowerShell smoke，校验输出能被 `ConvertFrom-Json` 解析。

### Phase 2: safe extraction and media

- [x] 实现 output path resolver、碰撞检查和原子写入。
- [x] 实现计数输出流、文件/字节/深度限额和取消。
- [x] 实现 `archive extract` 与 `--dry-run`。
- [x] 实现 `script extract` 及四种共享文本模式。
- [x] 实现 `image info` 和 `image convert`。
- [ ] 为成功、冲突、路径逃逸、超限、取消和 partial success 添加回归用例。

### Phase 3: headless format parameters

- [ ] 基于实际受阻格式定义最小 `IHeadlessResourceOptionsProvider`。
- [ ] 优先覆盖 KiriKiri/XP3 scheme、常见密码和已有非 GUI scheme 选择。
- [ ] 定义脱敏、options-file 和标准输入策略。
- [ ] 确认 GUI 参数控件仍通过原接口工作。

### Phase 4: SKILL and packaging

- [x] 使用 `skill-creator` 的 `init_skill.py` 创建 repo-local `garbro-cli`。
- [x] 生成与 SKILL 一致的 `agents/openai.yaml`。
- [x] 运行 `quick_validate.py`。
- [ ] 用代表性自然语言请求做 forward tests，并按失败模式收紧 SKILL。
- [x] 更新 `build.ps1`、NSIS、README、项目结构和 build/verify 文档。
- [x] 在设置页增加 SKILL ZIP 保存入口，并将四份任务参考打进分发包。
- [ ] 增加新 CLI Debug/Release 和安装包 smoke。

### Phase 5: stabilization

- [ ] 收集 v1 使用中的错误码、性能和参数缺口，不在 v1 内破坏字段。
- [ ] 为批处理和恢复 manifest 设计增量协议。
- [ ] 评估本地 MCP wrapper；只有明确收益时实现。
- [ ] 满足兼容期和迁移门槛后，再决定旧命令行工具的去留。

## Validation Checklist

### Build and discovery

```powershell
.\build.ps1 -Configuration Debug -NoPackage -NoVersionStamp -Smoke
.\build.ps1 -Configuration Release -NoPackage -NoVersionStamp -Smoke
bin\Debug\Onachi-GARbro.Cli.exe capabilities --output json
bin\Debug\Onachi-GARbro.Cli.exe formats list --kind all --output json
```

- [x] JSON 均可通过 PowerShell `ConvertFrom-Json`。
- [x] ArcFormats、Legacy、Experimental 和脚本格式均被预期发现。
- [x] 缺少可选 ArcExtra 时 capabilities 明确报告，而不是启动失败。

### Protocol

- [ ] 每个命令有成功、usage、unrecognized、needs_input 和 internal error golden
      response。
- [x] JSONL 每行独立可解析，最后一行总是 summary/error。
- [x] 标准错误日志不会污染标准输出。
- [ ] 未知可选字段不会使 SKILL 失败。

### Safety

- [ ] 测试 `../`、绝对路径、盘符、UNC、保留名和规范化后重名。
- [ ] 测试 existing file 在 `never`、`skip`、未来 `replace` 策略下的行为。
- [x] 测试声明大小正常但实际解压超限。
- [ ] 测试 max files、total bytes、entry bytes、depth 和 Ctrl+C。
- [x] 验证失败不留下最终文件，临时文件可清理。

### End-to-end

- [x] 普通归档：probe、list、dry-run、选择性提取。
- [ ] 受保护归档：稳定返回 needs_input，再用显式参数成功。
- [x] 脚本：filtered/raw/dump/jsonl 与格式声明一致。
- [x] 图片：info 与 convert 的 metadata/output 一致。
- [ ] GUI 仍可启动并完成受影响路径。
- [x] 旧 Console 与 Image.Convert smoke 继续通过。

## Progress

- 2026-07-26：完成现状审计，确认现有 Console、Image.Convert、脚本提取和
  `ParametersRequest` 可作为新架构基础。
- 2026-07-26：确定“稳定 CLI 为能力边界、SKILL 为薄编排层”的长期方向。
- 2026-07-26：创建本 active ExecPlan。
- 2026-07-27：完成独立 `GARbro.Cli` 的 v1 只读命令、安全提取、脚本导出和
  图片转换，实现非交互 `needs_input` 与稳定退出码。
- 2026-07-27：新增 PowerShell E2E；Debug/Release 各通过 173 个断言，覆盖
  合成恶意 ZIP、实际字节超限、加密 ZIP、四种 KiriKiri 模式，以及
  `I:\TempDays` 中真实 YPF/JPEG 样本。样本本体未进入仓库。
- 2026-07-27：创建并验证 `.codex/skills/garbro-cli`，同步 README、架构、
  build/verify、build smoke 和 NSIS 清单。
- 2026-07-27：Release 全解与 CLI/Console/Image.Convert smoke 通过；NSIS
  成功生成安装包，SHA256 为
  `C3E39C4C54F0C17AF276F6EB3D0B164D53CC8C02B303D0E43C0FA07EBFE46E4E`；
  `garbro-cli-skill.zip` 的 SHA256 为
  `A7B16F4E342D8DAD605AC4B3200C86334B6F2AD31A3FBFA1D7AA5D6E59803105`。
- 2026-07-27：NSIS 新增默认不勾选的 system PATH 组件；安装时只记录自身新增的
  条目，卸载时据此清理。进程级测试覆盖 add/already-present/remove，未改动真实
  用户或系统 PATH。
- 2026-07-27：`garbro-cli` SKILL 定位顺序扩展为仓库 Release、Debug、当前
  PATH、Program Files 安装目录。
- 2026-07-27：将 `garbro-cli` SKILL 拆成短入口和命令、脚本文本模式、机器协议、
  提取安全四份 reference；尤其固定 `--mode jsonl` 与 `--output jsonl` 的不同
  目标和 schema。
- 2026-07-27：GUI 设置新增 “AI integration”，只把安装包内的
  `garbro-cli-skill.zip` 保存到用户选择的位置，不推断或修改 Codex home。
  ZIP/保存测试通过 39 个断言，覆盖完整内容哈希、安全路径和原子覆盖。

## Decision Log

### 2026-07-26: 新增独立 CLI，而不是直接扩展旧 Console

旧 Console 的用途和文本行为可继续作为兼容与 smoke 基线。独立项目允许从第一天
冻结机器协议、安全默认值和退出码，不必让旧参数语法承担兼容负担。

### 2026-07-26: CLI 优先于 MCP

CLI 可以被 Codex、普通 shell、CI 和未来 MCP 共同复用，部署与调试成本最低。
MCP 若先于稳定 application contract，会复制错误处理和路径安全逻辑。

### 2026-07-26: SKILL 保持薄，不携带解包脚本

格式识别和文件写入需要确定性与严格安全边界，应留在经过构建验证的 C# 程序中。
SKILL 只保存 GARbro 特有的调用顺序和授权规则。

### 2026-07-26: JSON 与 JSONL 并存

小结果使用单 JSON envelope 更容易调用；大型目录和提取进度使用 JSONL，避免
内存累积并允许调用方增量消费。

### 2026-07-26: 参数支持渐进覆盖

GARbro 历史格式的强类型 options 差异很大。v1 先可靠返回 `needs_input`，再按
真实需求添加 headless provider，优于用反射或字符串猜测所有格式参数。

### 2026-07-26: 首期不承诺跨平台

当前解决方案、依赖和验证体系以 Windows/.NET Framework 为中心。CLI 的协议应
保持平台中立，但实现迁移需另立计划，不能成为 v1 的阻塞项。

### 2026-07-27: PATH 注册是可选且带所有权的安装组件

安装不应静默改动机器环境，因此组件默认不勾选。NSIS 字符串寄存器长度不足以安全
处理任意长 PATH，实际读写交给内置 PowerShell/.NET helper。只有 installer 确实
新增条目时才写入所有权标记，防止卸载误删用户原有配置。

### 2026-07-27: SKILL 以 ZIP 随应用分发，由用户决定安装位置

普通用户通常没有源码仓库，repo-local SKILL 不能作为唯一分发渠道。Release 构建
把完整 `garbro-cli` 目录打成一个顶层目录明确的 `garbro-cli-skill.zip`，NSIS
随程序安装该 ZIP。GUI 只负责让用户保存一个经过验证的本地副本，使用目标目录内
临时文件和原子替换，既不依赖网络，也不猜测 Codex、Claude Code 或其他代理工具
的技能目录。用户可先审阅 ZIP，再按所用环境的规则解压；因此分发与安装解耦，也
不会覆盖用户自行修改的 SKILL。

长期保持三层边界：

1. `Onachi-GARbro.Cli.exe` 是唯一确定性能力和安全边界。
2. `.codex/skills/garbro-cli` 是可版本控制、可验证的代理操作手册源目录。
3. `garbro-cli-skill.zip` 是跨环境分发物；设置页只导出，环境注册由用户或对应
   代理平台完成。

## Outcomes

当前已实现：

- GARbro 拥有一个安全、版本化、非交互的机器接口。
- AI 通过 repo-local 或用户从 ZIP 安装的自包含 SKILL 稳定完成探测、浏览、
  选择性提取和结构化文本转换。
- 安装器可由用户选择把 CLI 目录加入 system PATH，并在卸载时安全清理自身条目。
- GUI、旧 Console 和 Image.Convert 在迁移期保持兼容。
- 未来若增加 MCP、批处理或归档创建，复用同一协议与安全层。

当前剩余风险：

- 私有游戏格式缺少可提交的真实样本，必须建立本地样本验证记录。
- 部分格式把参数输入绑定在 WPF widget 上，headless options 需要逐个解耦。
- 解压炸弹限制必须约束实际写入流，单靠 archive metadata 不足。
- v1 协议一旦发布就需要兼容纪律，Phase 0 的 golden responses 不能省略。
