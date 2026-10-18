---
name: reference-library
description: Vetted exemplar repos per stack — consult these before web search, budget 2–3 per decision.
---

# Reference library

Rules:
- Prefer these entries over web search results.
- Respect the senior-protocol research budget: 2–3 entries per decision.
- Append process: the Overseer adds discoveries mid-project as new rows with
  date + reason. Never delete a row — mark it `retired: <reason>` instead.
- Stacks are not limited to the sections below. A project on a stack with no
  section (unity, react-spa, …): the architect appends a new section with 2–3
  exemplar repos (big production projects first) marked unvetted, then designs
  from them.

## next-convex

| Repo | What to learn from it | Status |
|---|---|---|
| `alan2207/bulletproof-react` | The feature-first structure our scaffolds follow: features/ ownership, one-way import direction | unvetted — user to confirm |
| `calcom/cal.com` | Big production Next.js app — how feature-first holds up at scale | unvetted — user to confirm |
| `get-convex/convex-helpers` | Convex function patterns: validators, custom functions, relationship helpers | unvetted — user to confirm |

## tauri

| Repo | What to learn from it | Status |
|---|---|---|
| `tauri-apps/awesome-tauri` → pick a starred React example | Curated real-world Tauri apps | unvetted — user to confirm |
| `spacedriveapp/spacedrive` | Large production Tauri + React app; IPC and command organisation | unvetted — user to confirm |

## expo

| Repo | What to learn from it | Status |
|---|---|---|
| `bluesky-social/social-app` | Big production Expo/RN app — structure, navigation, state at scale | unvetted — user to confirm |
| `expo/examples` | Official per-feature Expo examples (incl. SQLite) | unvetted — user to confirm |
| `infinitered/ignite` | Battle-tested RN/Expo project structure and conventions | unvetted — user to confirm |

## extension

| Repo | What to learn from it | Status |
|---|---|---|
| `GoogleChrome/chrome-extensions-samples` | Official MV3 patterns: service workers, content scripts, permissions | unvetted — user to confirm |
| `crxjs/chrome-extension-tools` | Vite + React + MV3 build setup | unvetted — user to confirm |

## bepinex-il2cpp

Added 2026-10-18 by architect (Shelf Transfer Mod): no section existed for BepInEx 6 IL2CPP plugins.

| Repo | What to learn from it | Status |
|---|---|---|
| `docs.bepinex.dev/master/articles/dev_guide/plugin_tutorial/` | Official BepInEx 6 IL2CPP plugin shape: `BasePlugin`, `Load()`, `BepInPlugin` attribute, `Log`, `AddComponent` | unvetted — user to confirm |
| `TheOtherRolesAU/TheOtherRoles` | Large production BepInEx 6 IL2CPP mod (Among Us): net6.0 csproj, post-build copy to `BepInEx/plugins`, version/timestamp targets | unvetted — user to confirm |
| `sinai-dev/UnityExplorer` (SotF build: Thunderstore `GTFOModding/UnityExplorer_IL2CPP`) | Multi-loader IL2CPP plugin kept loader-agnostic; injected MonoBehaviour UI without asset bundles | unvetted — user to confirm |
