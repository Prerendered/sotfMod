---
name: scaffold-next-convex
description: Default web stack — Next.js + Convex + shadcn/ui + Tailwind + Bun. Load when dispatched as architect on this stack.
---

# Scaffold — next-convex

## Setup

```bash
bun create next-app@latest . --typescript --tailwind --app --src-dir
bun add convex
bunx shadcn@latest init
bunx convex dev --once   # generates convex/ and env
```

## Folder tree (feature-first, big-project layout)

```
.
├── src/
│   ├── app/                # routes only — thin, compose features, no logic
│   │   ├── layout.tsx
│   │   ├── page.tsx
│   │   └── globals.css
│   ├── features/           # one folder per domain feature
│   │   └── <feature>/
│   │       ├── components/ # UI owned by this feature
│   │       ├── hooks/      # hooks owned by this feature
│   │       ├── lib/        # business logic owned by this feature
│   │       └── <feature>-types.ts
│   ├── components/
│   │   └── ui/             # shadcn/ui + shared primitives only — generated, do not hand-edit
│   ├── hooks/              # cross-cutting hooks only (use-*.ts)
│   ├── lib/                # cross-cutting only: result.ts, utils shared by 3+ features
│   └── constants.ts        # SCREAMING_SNAKE_CASE, grouped by domain
├── convex/
│   ├── schema.ts           # single source of truth for the data model
│   └── <domain>.ts         # queries/mutations grouped by domain
├── tests/                  # mirrors src/ exactly
├── tsconfig.json           # strict config from skills/standards
├── biome.json
├── tailwind.config.ts
└── package.json            # scripts: dev, build, typecheck, test, check
```

## Structure rules

- Import direction is one-way: `app/` → `features/` → shared (`components/ui`,
  `hooks/`, `lib/`). Never feature → feature internals; if two features need
  the same code, promote it to shared.
- A route file composes feature components and passes params — nothing else.
- New code starts inside its feature folder; promote to shared only at 3+
  feature uses (standards §14 Colocation).
- Delete empty layers: a feature with one component is just
  `features/<feature>/components/<name>.tsx` — don't pre-create hooks/lib.

## Required config

- `tsconfig.json`: the strict config from `.claude/skills/standards/` §1, plus `"paths": { "@/*": ["./src/*"] }`.
- `package.json` scripts: `"typecheck": "tsc --noEmit"`, `"check": "biome check ."`.

## Stack-specific standards

- Every Convex function declares `args` with `v.*` validators — no exceptions.
- `convex/schema.ts` defines all tables with validators and indexes; query by
  index, never `.filter()` on large tables.
- Mutations validate business rules server-side — never trust the client.
- Components call Convex only through `useQuery` / `useMutation` hooks.
- Auth checks live in Convex functions, not in components.

## Failure classes for tester

- Unvalidated Convex args accepting bad shapes.
- Race: two mutations on the same document (last-write-wins surprises).
- Query without index degrading on table growth.
- Client-side auth check with no server-side enforcement.
- Cross-feature import bypassing the promotion rule (coupling debt).
