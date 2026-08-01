# GARbro CLI 大规模资源工作流加固

## Context

2026-08-01 的九部柚子社作品全量工作流反馈表明，GARbro 的底层 XP3、
Hx v4 与图片解码能力可用，但正式机器 CLI 尚不能独立完成复杂 scheme 组合、
重复逻辑条目全量物化、可恢复提取和单进程批量图片转换。受影响的公开边界主要在
`Cli/`，少量必要的类型安全入口位于 `ArcFormats/KiriKiri/` 与 `GameRes/`。

本计划延续 `2026-07-26-garbro-cli-ai-skill.md` 中尚未完成的 headless 参数、
批处理与恢复里程碑。实现必须保持 `garbro.cli/v1` 的兼容规则、安全默认值、
JSONL 终止事件、目标路径包含性和原子写入。

当前工作区已有 5 个 `Properties/AssemblyInfo.cs` 版本号改动；这些改动不属于
本任务，实施和验证过程中必须保留且不得覆盖。

相关入口：

- `Cli/ArchiveCommands.cs`, `Cli/Safety.cs`, `Cli/RuntimeContext.cs`
- `Cli/ResourceCommands.cs`, `Cli/HxV4Commands.cs`
- `ArcFormats/KiriKiri/ArcXP3.cs`, `HxCrypt.cs`, `HxNameGenerator.cs`
- `tests/Cli/Invoke-CliTests.ps1`
- `docs/reference/cli-machine-interface.md`
- `.codex/skills/garbro-cli/**`

## Acceptance Criteria

- [x] `probe`、`archive list`、`archive plan` 和 `archive extract` 可显式使用
      XP3 `--scheme`，并可组合 `--hx-names` 与 `--cx-dump-dir`；结果回显不含
      密钥的 scheme identity，错误使用稳定 code/details。
- [x] `archive schemes`/`scheme-info`/`scheme-check` 可发现并验证一般 XP3
      scheme，包括受支持的内置别名，而不序列化加密密钥。
- [x] `archive list` 和提取事件包含稳定的 0-based `entryIndex`；默认重复策略
      仍拒绝碰撞，`--duplicate-policy suffix-index` 可确定性地物化全部逻辑条目。
- [x] `archive plan` 报告逻辑条目数、重复组、最大深度、声明尺寸统计、冲突和
      建议安全预算；`--budget auto` 使用同一份已打开 archive 计划派生有限预算。
- [x] `archive extract --manifest ... --checksum sha256` 产生版本化 JSONL source
      manifest；`--resume verify-size|verify-hash` 验证已有产物，已验证条目不把
      命令降级为 `partial_success`，缺失条目只补缺失部分。
- [x] JSON 模式对大规模文件结果保持有界，并提供 `--summary-only`；JSONL 持续
      逐项输出且不再额外保存完整 files/failures 集合。
- [x] `image convert-batch` 在一个进程内稳定枚举并转换目录，可递归、按签名发现
      无扩展名图片、保留相对目录、dry-run，并使用现有路径/限额/原子写入规则。
- [x] `COMMAND ACTION --help` 返回该 action 的完整 usage 和结构化 option schema。
- [x] Hx v4 archive generation 的失败包含 reason、尝试/可读 archive 数和建议动作；
      JSONL 进度具有结构化 phase/counts 且被节流。
- [x] 声明尺寸与 materialized bytes 的区别在协议、CLI 输出和 Skill 中明确。
- [x] Debug 构建、合成 CLI 回归、Skill 校验和相关 smoke 通过；无法使用商业样本
      验证的路径明确记录为限制。

## Implementation Checklist

- [x] 抽出类型安全的 XP3 scheme 解析/组合与归档打开入口，接入四个 archive/probe
      命令，并实现 scheme 发现与检查。
- [x] 抽出带 ordinal、重复组和确定性输出名的 archive planner，保持默认拒绝策略。
- [x] 实现 archive plan、自动预算、尺寸语义字段和 JSON 大结果有界策略。
- [x] 实现版本化 extraction manifest、流式 SHA-256 与 size/hash resume。
- [x] 实现顺序执行的单进程 image convert-batch；暂不声明未验证的并行能力。
- [x] 实现分层 help catalog 与 JSON option schema。
- [x] 为 Hx generation 增加机器可判定的错误 details、结构化且节流的进度。
- [x] 扩展合成 ZIP/图片 fixtures 和 CLI E2E，覆盖碰撞、恢复、manifest、plan、
      help、JSON/JSONL 有界行为与 extensionless 图片。
- [x] 同步 CLI reference、README/架构中受影响描述和 repo-local garbro-cli Skill。

## Validation Checklist

- [x] 变更前 `tests/Cli/Invoke-CliTests.ps1 -Configuration Debug`：306 assertions。
- [x] 使用 Visual Studio MSBuild、`/p:PreBuildEvent=` 构建 Debug solution。
- [x] `tests/Cli/Invoke-CliTests.ps1 -Configuration Debug` 全部通过。
- [x] `capabilities`、细分 help、archive scheme/plan、重复提取与图片 batch 定向 smoke。
- [x] `Onachi-GARbro.Console.exe -l` 与 `Onachi-GARbro.Image.Convert.exe -l` 通过。
- [x] repo-local `garbro-cli` Skill validator 与安装包 source/package 一致性测试通过。
- [x] 如本机证据路径可用，以只读方式验证至少一个真实 XP3 scheme、重复包和
      extensionless 图片流程；不复制或提交商业资源。

## Progress

- 2026-08-01：完整阅读九作反馈，建立持续目标并完成 CLI、XP3、Hx、提取安全、
  图片批处理、帮助系统和文档的并行只读审计。
- 2026-08-01：确认当前 JSONL extract 仍额外 O(n) 累积结果；重复路径验证与路径
  预留耦合；XP3 强制 scheme 入口为跨程序集不可见；Hx JSONL 已有未节流的人类
  文本进度而不是完全没有进度。
- 2026-08-01：变更前合成 CLI 回归通过 306 个断言。
- 2026-08-01：完成类型化 XP3 scheme/Hx/Cx 组合、方案发现与采样检查、post-Init
  material fingerprint、自动方案捕获和 lazy TPM 精确快照；显式 Hx 表最后覆盖。
- 2026-08-01：完成稳定 entry index、`suffix-index`、plan/finite auto budget、实际字节
  限额、流式 JSONL、large JSON warning，以及严格 UTF-8 的可恢复提取 manifest。
- 2026-08-01：manifest 现覆盖 crash tail、reparse point、hard-link COW、状态闭合、
  `not_attempted` 和已物化状态保留；图片 batch 覆盖签名发现、resume、WebP 类别边界。
- 2026-08-01：基于最新构建的二进制，全量 E2E 以 2,289 assertions/195.4 秒通过；
  50,001-entry 流式用例峰值低于 512 MiB。Skill 包测试通过 96 assertions。
- 2026-08-01：只读商业样本验证了 118-entry 受保护 XP3、51,248-entry voice 计划/
  dry-run 和 extensionless PNG；未复制样本、未写 Cx 输入目录。

## Decision Log

### 2026-08-01：保持 v1，以新增可选字段和新命令扩展

现有字段语义和退出码不需要破坏性修改。新命令、选项、事件字段、manifest schema
与错误 details 都可作为 v1 兼容扩展；source manifest 使用独立版本号。

### 2026-08-01：重复条目默认拒绝，仅显式 suffix 策略改名

默认 `error` 继续保护普通用户。`suffix-index` 使用 archive ordinal 生成稳定可逆
路径，同时在事件和 manifest 中保留原名、occurrence、offset 与 group size。
首轮不实现 `hash-dedup`，避免在 checksum/source manifest 契约稳定前丢失逻辑来源。

### 2026-08-01：批量图片首版顺序执行

单进程常驻已经消除逐文件启动和格式目录初始化成本。`FormatCatalog`、参数请求和
部分 handler 的并发可重入性尚未证明，因此首版不暴露虚假的 `--jobs` 并行承诺。

### 2026-08-01：显式 Cx dump 是有界只读输入，Hx overlay 最后应用

`--cx-dump-dir` 只在用户明确提供时调用严格 importer。相关日志名字在内存中应用，
不会读取或物化隐式 `HxNames.lst` 缓存；显式 `--hx-names` 总是在 Cx 结果之后覆盖。
路径重解析点、Cx 内容无效和 Hx 表无效分别使用稳定错误码。

### 2026-08-01：manifest 绑定规范化语义身份，而非参数拼写

同一个 resolved scheme 名称或同一个 builtin alias 的大小写（包括 XOR 十六进制大小写）
以及 `|garbro-importer` 兼容后缀可归一为同一身份；title 与 builtin、显式与 auto 仍然
不同。真正的 scheme material、Cx/Hx artifact 或 TPM bytes 变化必须改变 version-2
fingerprint 并拒绝恢复，防止同一输出目录混入不同解密上下文。

### 2026-08-01：在 archive Init 后捕获实际方案和懒加载工件

XP3 recognizer 的实际 `ICrypt` 在 `Init` 前被保存在 `Xp3Archive`，避免 Gensou 等方案
改变 per-entry cipher 后错误推断。懒加载 Cx TPM control block 以实际消费的 4,096
bytes 快照参与 post-Init fingerprint；无显式参数时也以 `auto_detected` 写入 plan 和
manifest。

### 2026-08-01：append-only manifest 保留物化事实并闭合选择集合

致命单项错误后，所有尚未验证的逻辑条目写为 `not_attempted`；此前已验证或已物化的
记录不会被后续审计行覆盖。完成的非 dry-run 满足
`selected = written + verifiedExisting + skipped + failed + notAttempted`。manifest 写入
使用同目录临时文件和原子替换来分离 hard link，resume 复制旧字节，fresh 从空文件开始。

## Outcomes

交付了完整的 XP3 typed/auto scheme 工作流、deterministic archive planner、有限自动
预算、可校验恢复 manifest、单进程图片 batch、结构化 help 和 Hx 进度/失败模型。
CLI 大规模流程不再依赖 GUI 预设或手工逐文件调度，且所有新增写路径保留既有默认拒绝、
路径包含、reparse、预算和原子提交保护。

验证结果：Visual Studio MSBuild Debug 全方案通过，仅保留 `Experimental` 的既有
`Microsoft.Win32.Primitives` 警告；最新二进制 E2E 2,289 assertions 全过；Skill validator、
96-assertion packaged-skill test、CLI/Console/Image.Convert smoke 和只读真实样本检查通过。

残余边界：图片批处理仍刻意顺序执行；真实 UAC/game launch 的 KrkrDump 采集未自动化；
`RequireExplicitManifestScheme` 在自动方案 finalize 成功时主要作为防御性兜底，可在后续
无行为变化的清理中评估移除。以上均不阻断本计划验收。
