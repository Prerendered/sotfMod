<!-- owner: Overseer · readers: everyone, every session -->
team-version: v1.0.0

# state.md — Shelf Transfer Mod (Sons of the Forest)

## Current phase
Setup COMPLETE (2026-10-18). Next: Task loop, Phase 1 (setup + discovery), board cards 1–9.
Gate 1 approved 2026-10-18. Source brief: Shelf-Transfer-Mod-Feature-Doc.pdf (v1.1).

## What was last worked on
- Architect (verified): team/architecture.md + monorepo scaffold, commit ad5fc3e. Deviations: Directory.Build.targets added; Core.Tests target net10.0 (no .NET 6 runtime here or on CI).
- cicd (verified on GitHub): origin = Prerendered/sotfMod (private). `.github/workflows/ci.yml` builds/tests/format-checks every mods/*/*.Core[.Tests] project, never Plugin/.sln. Branch protection on main: required check `check` (strict), PR required (0 approvals), admins enforced, no force push. PR #1 merged by User (merge commit 4cdb4a2, parents ad5fc3e + 453022b).
- seer: team/board.md, 30 cards over 6 phases. Overseer verified the §09 matrix mapping against the PDF (all 8 rows correct) and accepted seer's six judgment calls:
  1. card 2 composition root + per-feature installers (TransferFeature/LabelsFeature/UiFeature) and full ModConfig up front, so phases 2 and 3 are file-disjoint; card 2 adds them to architecture.md Structure.
  2. new log events (ui.*, transfer.request/cancel, shelves.list, gateway.fail); card 1 appends them to architecture.md F4.
  3. card 9 adds inv/free/can_hold to aim.change.
  4. card 19 (Core list builder) may run before the gate.
  5. card 16 owns Ui/Styles.cs, so card 18 depends on 16.
  6. disconnect leg (D4) in card 30 is `manual test — confirm` unless drivable.
- Game env (verified):
  - BepInEx be.755 IL2CPP win-x64 (commit 3fab71a, zip sha256 3616d6a6…478f) installed by Overseer with User permission. User launched once: BepInEx/interop has 238 assemblies (Assembly-CSharp, Endnight.*); LogOutput.log shows "Chainloader startup complete".
  - User set Steam to update only on launch.
  - Plugin build exit 0; post-build deployed ShelfTransfer.dll + ShelfTransfer.Core.dll to BepInEx/plugins/ShelfTransfer/.
  - UnityExplorer: the brief's ToniMacaroni/UnityExplorer_Sons 4.9.4 is a RedLoader build (Mods/, Libs/, manifest.json), unusable on BepInEx. Installed yukieiji/UnityExplorer v4.13.6 `UnityExplorer.BepInEx.Unity.IL2CPP.CoreCLR.zip` (sha256 dee2c456…ce95) into BepInEx/plugins/sinai-dev-UnityExplorer/ with User permission. Not yet seen loading in game.
- No-AI-attribution rule (User, 2026-10-18): in standards §10 + Overseer memory. PR #1 body cleaned.

## In progress (resume here)
- Setup docs (board.md, state.md, standards §10) committed on branch `setup-board` → PR for User to merge (Overseer cannot merge: blocked by permission classifier).
- Recommend /compact before starting the task loop (phase gate).

## Up next
- Phase 1: card 1 (mod log, D5) and card 2 (composition root) first; card 3 discovery dump depends on 1; card 4 = User discovery session in game.
- Core-only cards 5–7 (amount clamp, runner, failure paths) can run in parallel with Plugin cards, worktree-isolated.

## Open questions / blockers
- ⚠ PENDING User decision (A/B): main has Claude co-author trailers on ad5fc3e (Fable) and 453022b (Opus; PR #1 merged before the cleaned 92a53ec landed). A = lift protection, rewrite history without trailers, force-push, restore protection. B = leave. Every later commit is clean.
- Resolved: GitHub private repo, CI builds Core only.
- Resolved: in-game test per card + mod log.
- Resolved: monorepo layout (mods/ShelfTransfer/{Core,Plugin,Core.Tests}, shared/ empty), two DLLs, no merge.

## Files touched recently
- team/state.md, team/plan.md, team/board.md (seer), team/architecture.md (architect)
- .claude/skills/standards/SKILL.md — §10 no-AI-attribution rule
- .claude/skills/reference-library.md — architect appended `bepinex-il2cpp` (UnityExplorer row points at a GTFO build; correct SotF-usable build is yukieiji v4.13.6)
