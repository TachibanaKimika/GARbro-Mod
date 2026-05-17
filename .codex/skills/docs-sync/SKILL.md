---
name: docs-sync
description: Audit whether code, build, format-support, or workflow changes in GARbro-Mod-Onachi require updates to README.md, AGENTS.md, PLANS.md, docs/architecture, docs/reference, docs/supported.html, or repo-local skills. Use before submitting changes or when the user asks for documentation synchronization.
---

# Docs Sync

Use this skill to keep repository knowledge aligned with source changes. Report
first unless the user explicitly asks you to edit docs.

## Primary Targets

- `README.md`: user-facing application behavior.
- `AGENTS.md`: short agent entry point and mandatory rules.
- `PLANS.md` and `docs/exec-plan/**`: long-running work plans.
- `docs/architecture/**`: stable module boundaries and ownership.
- `docs/reference/**`: build, restore, run, release, troubleshooting, and
  operational notes.
- `docs/supported.html`: published supported-format documentation. Update only
  when format support documentation is explicitly in scope or the update
  workflow is clear.
- `.codex/skills/**`: repeatable agent workflows.

## Workflow

1. Resolve scope:

   - Prefer explicit user scope.
   - Otherwise inspect the current worktree.
   - For branch comparison, use `origin/main` then `origin/master` as fallback.

2. Build a feature inventory from changed files:

   ```powershell
   git status --short --untracked-files=all
   git diff --stat
   git diff --name-status
   ```

3. Classify changes:

   - User-visible behavior: update `README.md` or relevant reference docs.
   - Supported resource formats: consider `docs/supported.html` and reference
     notes.
   - Build, restore, packaging, release, or prerequisites: update
     `docs/reference/build-and-verify.md`.
   - Architecture or project boundary changes: update
     `docs/architecture/project-structure.md`.
   - Agent workflow changes: update `AGENTS.md` or `.codex/skills/**`.
   - Multi-step work: update or create an ExecPlan.

4. Do a doc-first pass:

   - Read the likely existing docs.
   - Identify stale, missing, or misleading claims.

5. Do a code-first pass:

   - Map each user/operator/agent-facing change to a durable doc location.
   - Prefer updating existing docs over creating new files.

6. Produce a Chinese report with:

   - scope
   - inspected docs
   - required updates
   - optional updates
   - no-doc-impact rationale, if applicable

7. If editing is authorized, make focused documentation changes and re-read the
   changed files.

## Output Shape

```text
### 文档同步检查

### 范围
- Source: <worktree/ref>
- Target: <ref if any>

### 必须同步
1. <path>
   - 原因: <behavior/build/architecture/format/workflow change>
   - 证据: <source file or diff signal>

### 可选同步
1. <path>
   - 原因: <why optional>

### 无需同步
- <reason>
```
