---
name: standards
description: Global coding standards — load before writing or reviewing any code. Migrated from the Operating Manual _standards.md; this skill is the source of truth.
---

# Global Coding Standards

## 0. Precedence

This skill outranks repo precedent. When existing code contradicts a rule here,
follow the rule or ask the Overseer — never silently copy the existing pattern
because "that's how the repo does it". Inheriting debt is a deviation, and a
deviation needs a recorded reason (senior-protocol rule 4). This applies to test
location, comment style, and everything below.

Scope: §§1–3, 6–9, 11–13, and 15 assume the default TypeScript stack. On any
stack (Unity/C#, plain React, …) these always apply: §4 Comments, §5 File
headers (adapt comment syntax), §10 Git, §14 Design principles, §16 Done
checklist (substitute the stack's own typecheck/lint/test commands). For
non-TS stacks the architect records the equivalents — naming, lint, test
runner — in team/architecture.md.

## 1. TypeScript

Strict config (non-negotiable):

```json
{
  "compilerOptions": {
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "noImplicitOverride": true,
    "noImplicitReturns": true,
    "exactOptionalPropertyTypes": true,
    "isolatedModules": true,
    "target": "ES2022",
    "moduleResolution": "bundler"
  }
}
```

Rules: No `any`. No `!`. No enums — use `as const` or discriminated unions. No default exports outside framework files. Explicit return types on all exported functions. `import type` for type-only imports. `satisfies` over `as`. Never `React.FC`.

## 2. File naming

All filenames and folders: kebab-case. Barrel `index.ts` only when imported in 3+ places.

| Content | Example |
|---|---|
| Component | `workout-editor.tsx` |
| Hook | `use-current-user.ts` |
| Utility | `format-duration.ts` |
| Types | `workout-types.ts` |
| Test | `workout-editor.test.tsx` |
| Config | `tailwind.config.ts` |

## 3. Naming conventions

Functions: verb + noun, camelCase, no abbreviations.

| Verb | Use for |
|---|---|
| `get` | Read single item |
| `list` | Read collection |
| `create` | Insert record |
| `update` | Modify record |
| `delete` | Remove record |
| `format` | Transform for display |
| `parse` | Transform raw input |
| `validate` | Check validity |
| `calculate` | Compute derived value |
| `build` | Construct complex object |
| `handle` | Event handler only |
| `on` | Callback prop only |

Booleans: `is` / `has` / `can` / `should` / `did` prefix always.

Constants: `SCREAMING_SNAKE_CASE`. All live in `src/constants.ts`, grouped by domain with `// ─── Label ───` dividers. Design tokens (colors, spacing, radii) are not constants — they live in Tailwind config.

## 4. Comments

Comments explain **why**, never **what**. If you need to explain what the code does, rename it.

Comment for: business rule rationale, non-obvious workarounds, external links, TODO/FIXME with owner.

Never comment: restating the code, self-documenting TS, section dividers inside components.

Keep them short. One or two lines. No multi-paragraph module docs or block banners — a comment that runs to a paragraph is a design smell: split the code or move the rationale to the Decisions block. The header in §5 is the only structured doc a file carries.

TODO format: `// TODO(user): <what and why deferred>` — owner always required.

## 5. File headers

Every file gets a header after `'use client'` and before imports. Line comments only.

```typescript
// purpose: <one sentence>
// owns:    <key exports>
// deps:    <key internal dependencies>
// caution: <optional — real gotchas only>
```

`purpose`, `owns`, `deps` always present. `caution` only when there is a genuine non-obvious constraint.

## 6. Components

File order: `'use client'` → external imports → internal imports → local types → named export component (hooks → derived state → handlers → early returns → render).

Rules:
- One component per file. Named export only.
- No inline styles — Tailwind only.
- No magic numbers — `src/constants.ts`.
- No DB/API calls — through hooks or server actions.
- No business logic — lives in the feature's `lib/`, shared `lib/`, or `convex/`.
- Props type above component, never inline.
- Max 150 lines. `'use client'` only when needed.
- No prop drilling beyond 2 levels.
- Forms: `react-hook-form` + `zod`. Modals: Esc + backdrop dismissible. Loading: skeletons.

## 7. Hooks

One hook per file. Returns object not array (except simple toggles). Explicit return type always. No raw DB/fetch calls.

## 8. Error handling

Default: Result type. Errors are values, not exceptions.

```typescript
export type Result<T, E = AppError> =
  | { ok: true; value: T }
  | { ok: false; error: E }

export const ok = <T>(value: T): Result<T> => ({ ok: true, value })
export const err = <E = AppError>(error: E): Result<never, E> => ({ ok: false, error })
```

try/catch allowed only at top-level boundaries (route handlers, framework glue). Never nested. Never in loops or hot paths. Never swallow silently. Always use `ok()` / `err()` helpers.

## 9. Tests

Runner: Bun. Tests live in `tests/` mirroring `src/` exactly — never co-located. Use `@/` imports. `describe` by function, `it` as sentence. No snapshot tests.

Separate test tree on **every** stack (not just TS) — this rule survives repo precedent (§0). A separate file/dir, never inline with source: no Rust `#[cfg(test)] mod tests` in the source file, no `Component.test.tsx` beside the component. The architect records the stack's separate-tree convention (e.g. Rust `tests/` integration crate, or a dedicated `*_test` module tree) in team/architecture.md; if the ecosystem's idiom is genuinely inline, that is a deviation with a recorded reason, not a default.

Ownership: the tester agent authors all tests (unit + criteria). Dev never writes tests — dev's obligation is to keep the existing suite green.

## 10. Git

Branches: `<feature-name>` kebab-case, no type prefix. One concern per branch. Branch off `main` only. Delete after merge. Never commit directly to `main`.

Commits: `<type>: <description>` — imperative, lowercase, no period. Squash merge to `main`. PRs require passing typecheck + tests.

Commit body: terse. The subject line alone is enough for most commits; add a body only to record *why* a non-obvious change was made, in one or two lines — never a multi-paragraph changelog restating the diff.

## 11. Path aliases

`@/` always over deep relative paths. `{ "paths": { "@/*": ["./*"] } }`

## 12. Import order

5 groups, blank line between each: Node built-ins → external packages → internal shared/lib → internal types → local same-folder.

## 13. Restrictions (never break)

No commits to `main` directly. No `any`. No inline styles. No secrets in code. No `console.log` committed. No default exports outside framework files. No business logic in components. No DB calls in components. No nested try/catch. No try/catch in hot paths. No `@ts-ignore` without comment. No barrel `index.ts` unless imported 3+ places.

## 14. Design principles

- **KISS**: Simplest solution that works. Exception: measurable perf/UX improvement earns complexity — but only when you can point to the specific problem it solves right now.
- **YAGNI**: Build for the current story. Generalise at the second real use case, not before.
- **Single Responsibility**: One file, one function, one component — one job. Split if you need "and".
- **Composition over Inheritance**: Compose with hooks and components. Never extend base classes.
- **Fail Fast**: Validate at the boundary. Zod on all external input. `v.*` on all Convex args. Never `null` to signal failure.
- **Colocation**: Keep code close to where it's used — new code starts in its feature folder. Promote to shared `lib/` at 3+ feature uses. Duplication is cheaper than the wrong abstraction.

## 15. Post-change checklist

Run after every code addition or edit. Fix violations inline — never report and leave for the user.

**Step 1 — Biome**: `bun run check` — must exit zero errors before self-review.

**Step 2 — Self-review**:

```
File: header present, imports in 5-group order
TS: no any, no !, explicit return types, import type used
Components: no inline styles, no business logic, no DB calls, no prop drilling >2, <150 lines, use client only if needed, named export
Naming: verb+noun functions, boolean prefixes, kebab-case files/folders
Constants: new constants in correct section of constants.ts, no design tokens in constants
Errors: Result type used, no nested try/catch, no silent swallowing
Tests: existing suite passes; no tests deleted or weakened (authoring: tester agent)
Security: no secrets, no sensitive data in URLs
```

## 16. Before marking a task Done

```
[ ] bun run typecheck — zero errors
[ ] bun test — zero failures
[ ] Post-change checklist passed
[ ] Acceptance criteria fully met
[ ] Branch clean — no unrelated changes
[ ] team/state.md updated
```
