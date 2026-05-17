# Execution Plans

Use an ExecPlan when a task is cross-cutting, risky, long-running, or likely to
need handoff across turns. Small single-file fixes do not need one.

## Storage

- Active plans live in `docs/exec-plan/active/`.
- Completed plans move to `docs/exec-plan/completed/`.
- Name files with date plus topic, for example
  `docs/exec-plan/active/2026-05-17-format-handler-hardening.md`.

## Required Sections

Each plan should include:

1. `Context`: current facts, relevant files, and user goal.
2. `Acceptance Criteria`: observable completion conditions.
3. `Implementation Checklist`: concrete editable steps.
4. `Validation Checklist`: commands, sample files, and manual checks.
5. `Progress`: timestamped status updates.
6. `Decision Log`: non-obvious choices and tradeoffs.
7. `Outcomes`: final result, residual risk, and follow-up work.

## Rules

- Keep plans executable. Every checklist item should be small enough for an
  agent to complete or verify directly.
- Update the plan as work proceeds; do not leave stale intent after code changes.
- Move a finished plan to `completed/` in the same change that completes it.
- If a task changes architecture, supported behavior, build process, or format
  coverage, link the plan to the matching `docs/architecture/**` or
  `docs/reference/**` update.
