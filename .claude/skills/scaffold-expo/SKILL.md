---
name: scaffold-expo
description: Mobile stack — Expo + expo-router (+ expo-sqlite when offline-first). Load when dispatched as architect on this stack.
---

# Scaffold — expo

## Setup

```bash
bunx create-expo-app@latest . --template default   # includes expo-router + TS
```

## Folder tree (feature-first, big-project layout)

```
.
├── app/                    # expo-router file routes only — thin, compose features
│   ├── _layout.tsx
│   └── index.tsx
├── src/
│   ├── features/           # one folder per domain feature
│   │   └── <feature>/
│   │       ├── components/
│   │       ├── hooks/
│   │       ├── lib/
│   │       └── <feature>-types.ts
│   ├── components/
│   │   └── ui/             # shared primitives only
│   ├── hooks/              # cross-cutting hooks only
│   ├── lib/                # cross-cutting: result.ts + db.ts (single SQLite access point)
│   └── constants.ts
├── tests/                  # mirrors src/ exactly
├── tsconfig.json           # strict config from skills/standards
├── app.json
└── package.json
```

## Structure rules

- Import direction is one-way: `app/` routes → `features/` → shared. Never
  feature → feature internals; promote shared code at 3+ uses.
- A route file renders feature screens and wires navigation — nothing else.
- Delete empty layers: don't pre-create hooks/lib inside a feature.

## Stack-specific standards

- SQLite access through one `src/lib/db.ts` module with typed queries;
  migrations run at startup, versioned in one place.
- All user input validated with zod before touching the DB.
- No platform forks inline — `Platform.select` wrapped in lib helpers.
- Test on both platforms before Done; typecheck via `tsc --noEmit`.

## Failure classes for tester

- SQLite migration failing on an existing install (upgrade path).
- Back-gesture/navigation state loss on Android.
- Keyboard covering inputs on small screens.
- Offline writes lost when the app is killed mid-transaction.
- Cross-feature import bypassing the promotion rule (coupling debt).
