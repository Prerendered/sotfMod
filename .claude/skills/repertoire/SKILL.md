---
name: repertoire
description: The cross-project memory — how the archivist saves a finished project and how the Overseer and architect fetch past projects as precedent. Load when writing an entry or consulting the repertoire.
---

# Repertoire

A separate git repo of finished-project entries. Each new project consults it for
precedent — proven stacks, patterns, and pitfalls — so setup goes fast.

## Repo layout
```
Projects/
├── README.md            ← ToC to all projects
└── <Category>/          ← domain folder, e.g. Food/
    └── <project>/       ← e.g. meal-planner/
        └── README.md    ← the project entry
```

ToC row format:
```
| Project | Category | Stack | Tags | One-liner | Date |
```
Tags are the match surface — 3–6 lowercase keywords (domain, platform, key libs) so a
single ToC fetch finds precedent without opening any entry.

## Entry template (`<Category>/<project>/README.md`)
```markdown
# <project>
> team-version: vX.Y · completed: YYYY-MM-DD · stack: <stack>
## Problem
## Core loop
## Stack & key dependencies
## Architecture summary        (3–6 lines + a link-free flow sketch)
## Decisions                   (verbatim Decisions lines, incl. rejected alternatives)
## Patterns that worked        (from architecture.md patterns, review-report passes)
## Pitfalls / retro            (from review/test/security FAILs and escalations)
## References used             (reference-library rows this project consumed)
```
Every section traces to a team/ artifact. No invented content.

## Retrieval — fetch via raw URLs, never clone to read
The read path (Overseer at intake, architect for references) uses raw fetches; only
the archivist clones, and only to push.

- Derive the raw base from the configured `repertoire:` URL:
  - `https://github.com/<user>/<repo>(.git)` → `https://raw.githubusercontent.com/<user>/<repo>/main/`
  - `git@github.com:<user>/<repo>.git` → same `raw.githubusercontent.com/<user>/<repo>/main/`
- Intake / Onboarding: one curl for the ToC README; match on the Tags and Stack
  columns; then curl at most 2 matched entry READMEs — the senior-protocol research
  budget (2–3) applies.
- Private repos: `curl -H "Authorization: token $(gh auth token)"` — never a hardcoded
  token, never a token written to any artifact.
- Fetch failure (offline, private without a token) → say so in one line and continue
  without precedent. Never block intake on the repertoire.

## Rules
- Precedent is a proposed default, never law — the User confirms; every deviation is
  recorded in the plan's or architecture's Decisions block.
- Secrets never enter an entry: env values, keys, tokens, private endpoints. Names of
  variables only.
