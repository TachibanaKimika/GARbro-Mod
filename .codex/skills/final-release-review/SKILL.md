---
name: final-release-review
description: Review current GARbro-Mod-Onachi changes before submitting or merging to the main branch. Audit the diff for build breakage, format recognition regressions, user-facing behavior changes, documentation drift, and release risk; produce a Chinese go/no-go report with evidence.
---

# Final Release Review

Use this skill for mainline readiness review. Stay evidence-based and actionable.

## Scope

Prefer a user-provided compare target. Otherwise use:

1. `origin/main`
2. `origin/master`
3. current upstream

If no target can be resolved, ask for one.

## Workflow

1. Prepare refs:

   ```powershell
   git fetch --prune origin
   git status --short --branch
   ```

2. Snapshot the diff:

   ```powershell
   git diff --stat <target>...HEAD
   git diff --name-status <target>...HEAD
   git log --oneline --reverse <target>..HEAD
   ```

3. Inspect high-risk areas first:

   - `GameRes/**`: core contracts, stream primitives, format catalog, extraction.
   - `ArcFormats/**`, `Legacy/**`, `Experimental/**`: recognizer precision,
     bounds checks, project file inclusion, sample behavior.
   - `*.csproj`, `GARbro.sln`, `Directory.Build.props`, `packages.config`:
     build graph and restore behavior.
   - `GUI/**`: WPF interaction and resource loading.
   - `Console/**`, `Image.Convert/**`, `SchemeTool/**`: CLI smoke behavior.
   - `docs/**`, `AGENTS.md`, `.codex/skills/**`: harness and docs consistency.

4. Run or cite verification:

   - Use `$garbro-build-verify` for build/smoke checks.
   - Use `$docs-sync` for documentation drift.
   - Use skill validation for `.codex/skills/**`.

5. Decide:

   - `GO`: no confirmed blocking issue.
   - `NO-GO`: concrete build break, confirmed regression, data-loss/security
     risk, missing migration for breaking behavior, or severe documentation
     mismatch that would mislead agents/operators.

Do not produce `NO-GO` for speculative risk alone. Provide targeted follow-up
checks when evidence is incomplete.

## Output Shape

All output should be in Chinese.

```text
### main 提交前风险审查

### 范围
- Source: <source>
- Target: <target>
- Diff: <target...source>

### 审查结论
- <GO | NO-GO>: <one sentence>

### 变更摘要
- <files changed and key areas>

### 风险评估
1. <finding title>
   - Risk: <LOW | MODERATE | HIGH>
   - Evidence: <specific file/diff/test signal>
   - Impact: <practical impact>
   - Action: <fix or validation>

### 验证
- <commands run or blockers>

### Follow-up
- <only actionable remaining items>
```
