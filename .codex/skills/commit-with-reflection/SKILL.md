---
name: commit-with-reflection
description: Use when the user asks to verify current changes, run pre-commit or regression checks, write a commit message, create a local commit, or push changes in GARbro-Mod-Onachi. Build local diff context, compare with the target branch, run the smallest sufficient verification for this legacy .NET Framework solution, and stop at the requested boundary.
---

# Commit With Reflection

Use this skill to produce a defensible verification or commit flow. Default to
verification-only unless the user explicitly asks to commit or push.

## Modes

- `verification-only`: inspect, verify, report, and stop.
- `commit`: verify, stage intended files, and create one local atomic commit.
- `push`: commit, re-check remote state, then push only when explicitly asked.

## Workflow

1. Determine the authorization boundary before changing git state.

2. Build local context:

   ```powershell
   git status --short --branch
   git status --short --untracked-files=all
   git diff --stat
   git diff --cached --stat
   ```

3. Resolve compare target in this order:

   - user-specified branch
   - current branch upstream
   - `origin/main`
   - `origin/master`

   Fetch when a remote compare is needed:

   ```powershell
   git fetch --prune origin
   ```

4. Summarize:

   - local changed files
   - target branch changed files
   - overlapping files
   - touched layers: `GameRes`, `ArcFormats`, `Legacy`, `Experimental`,
     `GUI`, `Console`, `Image.Convert`, `SchemeTool`, `docs`, `.codex/skills`
   - risk level: low, moderate, or high

5. Reflect before commit:

   - What behavior changed?
   - Which project boundaries changed?
   - What verification is required?
   - Is documentation synchronized?
   - Is the commit scope atomic?

6. Run the smallest sufficient verification:

   - `.codex/skills/**`: validate changed skills with the skill validator.
   - docs only: re-read changed docs for factual correctness.
   - build or toolchain changes: use `$garbro-build-verify`.
   - resource format changes: use `$garbro-format-authoring`, then build/smoke
     as far as samples allow.
   - GUI changes: build and manually verify the affected path when possible.

7. Stop after reporting in `verification-only` mode.

8. In `commit` or `push` mode, stage deliberately:

   ```powershell
   git add <intended-files>
   git diff --cached --stat
   git diff --cached
   ```

9. Write commit messages as `<type>: <Chinese summary>`.

   Common types: `feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`.

10. Push only in `push` mode after the commit succeeds and the worktree is in
    the expected state.

## Stop Conditions

- The user only asked for verification.
- The intended commit set cannot be isolated from unrelated user changes.
- Build failures cannot be distinguished from local environment issues.
- A high-risk regression is found.
- Push authorization is missing.
