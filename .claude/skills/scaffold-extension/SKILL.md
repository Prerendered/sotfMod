---
name: scaffold-extension
description: Chrome extension stack — Vite + React + Manifest V3 (crxjs). Load when dispatched as architect on this stack.
---

# Scaffold — extension

## Setup

```bash
bun create vite@latest . --template react-ts
bun add -D @crxjs/vite-plugin
```

## Folder tree

```
.
├── src/
│   ├── manifest.ts         # MV3 manifest via crxjs defineManifest
│   ├── background/         # service worker — event-driven, no state in memory
│   ├── content/            # content scripts, one folder per script
│   ├── popup/              # popup UI (React) — feature-first inside when it grows
│   ├── options/            # options page if needed
│   ├── features/           # domain logic shared across contexts, one folder per feature
│   ├── lib/                # cross-cutting only: result.ts + messaging.ts (typed message bus)
│   └── constants.ts
├── tests/
├── tsconfig.json           # strict config from skills/standards
├── vite.config.ts
└── package.json
```

## Structure rules

- Extension contexts (`background/`, `content/`, `popup/`, `options/`) are
  entry points only — domain logic lives in `features/`, imported by whichever
  context needs it. Never feature → feature internals; promote to `lib/` at
  3+ uses.

## Stack-specific standards

- MV3 service workers die at any time — persist all state to
  `chrome.storage`, never module-level variables.
- All messaging through one typed wrapper (`src/lib/messaging.ts`) with a
  discriminated-union message type — no raw `chrome.runtime.sendMessage`.
- Request the narrowest permissions and host_permissions that work.
- Content scripts stay thin — parse the page, message the worker, done.
- Production build (`bun run build`) must load unpacked without manifest errors.

## Failure classes for tester

- State lost on service worker restart.
- Message sent before the receiving context exists (port closed errors).
- Content script racing the page's own JS / running before DOM ready.
- Permissions missing for a host the feature actually touches.
