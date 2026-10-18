<!-- owner: Overseer · readers: everyone, every session -->
team-version: v1.0.0

# state.md — Shelf Transfer Mod (Sons of the Forest)

## Current phase
Setup (Gate 1 APPROVED 2026-10-18).
Was: Intake (greenfield). Source brief: Shelf-Transfer-Mod-Feature-Doc.pdf (v1.1, 18 Oct 2026).

## What was last worked on
- Read the brief. Ran read-only pre-flight checks:
  - Game at C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest. Clean install, no BepInEx yet.
  - No RedLoader markers (version.dll, _RedLoader). Clean.
  - .NET SDK: 10.0.201 only. It can build net6.0 through the targeting pack, so a .NET 6 SDK is not required.
  - git 2.45 and gh 2.100 available. Project folder is not a git repo yet.

## In progress (resume here)
- Gate 1 approved. Architect dispatched (background): architecture.md + monorepo scaffold, git init on main, no push.
- BepInEx be.755 IL2CPP win-x64 (commit 3fab71a, zip sha256 3616d6a6…478f) installed into game folder by Overseer with User permission. No file collisions. Interop NOT generated yet: needs one game launch.
- Waiting on: architect result; User to launch game once (generates BepInEx/interop) + set Steam "update only on launch".

## Up next
- After Gate 1: architect (scaffold Core + Plugin, mod log), cicd (opus, Core-only CI on Prerendered/sotfMod), seer (board). Designer skipped. User runs pre-flight step 0 in parallel.

## Open questions / blockers
- Resolved: GitHub private repo, CI builds Core only.
- Resolved: in-game test per card + mod log.
- Resolved: monorepo layout (mods/ShelfTransfer/{Core,Plugin,Core.Tests}, shared/ empty), two DLLs, no merge.

## Files touched recently
- team/state.md — initialized for this project
- team/plan.md — written for Gate 1
