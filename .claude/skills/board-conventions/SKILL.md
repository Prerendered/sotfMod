---
name: board-conventions
description: Story format, acceptance criteria rules, phases, and order numbers for team/board.md — load when creating or editing the board.
---

# Board conventions

## board.md template

```markdown
# Board — [Project]
| # | Story | Status | Priority | Phase | Files | Acceptance criteria |
|---|-------|--------|----------|-------|-------|---------------------|
```

- Status: `Backlog` / `In Progress` / `In Review` / `Done`. New stories always start `Backlog`.
- `#` is the Order number — dependency-sorted, see below.
- Files: the story's expected file footprint — the Overseer uses it for the
  parallel-dispatch check (shared file = sequential).
- WIP limit: never more than 2 cards In Progress — finish before starting new.

## Story format

- Title: `As a <role>, I can <action> <context>`
- Priority: High (core flow) / Medium (polish) / Low (nice-to-have)
- Notes: one-liner on implementation detail, component, or function

## Acceptance criteria rules

- 4–7 checkable conditions per story.
- Each condition independently verifiable (yes/no).
- Cover the happy path, at least one edge case, at least one technical/data requirement.
- No vague conditions like "works correctly" — specify the expected behaviour.
- A story is only Done when every criterion is checked.

## Sequencing phases

| Phase | What goes here | Priority |
|---|---|---|
| 1 — Foundation | Repo setup, auth, install flow, onboarding | High |
| 2 — Core flow | Minimum path through the primary use case | High |
| 3 — Core interactions | Supporting interactions that complete the primary flow | High |
| 4 — Session/state polish | Edge cases, close/end states, status visibility | Medium |
| 5 — Secondary features | Calendar, history, ratings, analytics | Medium |
| 6 — Nice to have | Settings, profile editing, leave/delete flows | Low |

## Order number rules

- Order reflects dependency — if B requires A, A gets a lower number.
- Tasks in the same phase ordered by user-facing impact.
- Integers starting at 1, no duplicates.
- When adding tasks mid-project, use decimals temporarily (e.g. 4.5).
