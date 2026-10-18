---
name: codebase-survey
description: How scout maps an unfamiliar repo — load in onboarding and brief modes. The method for turning existing code into architecture.md + map.md.
---

# Codebase survey

Goal: describe what the code **is**, never what it should be. Aspirations are
refactor cards, not map entries.

## Survey order

1. **Perimeter** — README, package manifest, config, scripts. Learn the declared
   stack, entry points, and build/run/test commands. Run them; record what works.
2. **Entry points** — `main`, route roots, `index`, app bootstrap. Trace outward
   from where execution actually starts.
3. **Module boundaries** — top-level folders and their one-line responsibility.
   Note the import direction between them (who depends on whom).
4. **Data flow** — for each core feature, trace input → processing → storage →
   output. Each becomes an architecture.md flow diagram.
5. **Conventions in force** — naming, error handling, state, tests. Sample 3+
   files; record the *actual* pattern and its exception rate, not the ideal.
6. **Danger zones** — high-fan-in modules, files with no tests, `TODO`/`HACK`
   clusters, anything the git log shows churning.

## Budget
Survey to the depth the dispatch scopes — a brief covers one subsystem; an
onboarding covers the perimeter plus core flows, not every file. Stop when the map
is actionable, not exhaustive (senior-protocol rule 2).

## map.md is the durable output
Onboarding writes it once; later briefs may append newly-charted subsystems. Never
let map.md drift from the code — a brief that finds it stale corrects it in place.
