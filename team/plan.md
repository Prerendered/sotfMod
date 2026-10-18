<!-- owner: Intake (main session) · readers: all agents -->

# Plan — Shelf Transfer Mod (Sons of the Forest)

Source of truth for feature detail: `Shelf-Transfer-Mod-Feature-Doc.pdf` (v1.1, 18 Oct 2026),
called "the brief" below. This plan adopts the brief and records every place it was amended.
Agents read this file. The brief's section numbers are cited as §NN.

## Problem
A Sons of the Forest storage shelf cannot be moved while it holds items. Emptying one means taking
items a few at a time and carrying them to another shelf. Reorganising a base takes many trips.
This is User's own pain, playing as a client on a friend-hosted session.

## Core loop
[look at an item slot on a shelf] → [press L, pick a target shelf and an amount, confirm] →
[the items are on the target shelf for both client and host, with no walking]

Side loop: [look at a shelf] → [press the label key, type a name, optionally pick a group] →
[that shelf is recognisable in the overlay and in the world]

## Invariants
Every flow and story is checked against these. A contradiction is a design defect.

1. **Conservation.** No item is ever duplicated or lost. At every step of a transfer,
   source + target + (at most 1 in the player's inventory) = the starting total. If an item
   is stranded in inventory (store refused, disconnect), the mod reports it. It never retries
   blindly.
2. **Vanilla path only.** Shelf state changes only through the game's own take and store calls,
   the same calls a player triggers with E and C. No direct writes to storage state, no
   host-side code, no custom network messages.
3. **Verify before next.** No take starts until the previous take and store are confirmed by
   re-reading counts. Any mismatch, refusal or exception stops the loop and produces a report.
4. **Never start what can't finish.** The amount is clamped to min(source stack, target free
   capacity). A target that cannot hold the item type cannot be selected. A transfer the player
   cannot physically perform (at carry cap, see D3) is blocked before it starts.
5. **Labels are local.** Labels and groups live only in a client-side JSON file. They never touch
   game state or the network.

## Scope (v1)
### In
- Storage shelves only (§01).
- One item type per transfer: the slot being aimed at.
- Transfer all (default) or a chosen amount.
- Client-side shelf labels and base groups, shown in the overlay and as world-space text when near.
- Works on a friend-hosted session where only the client runs the mod.
- A dedicated mod log file that Claude can read (D5).

### Out
- Moving a shelf with its contents.
- Other storage types (log holders, stick holders, drying racks).
- Any host-side component.
- Sharing labels or groups with other players.
- Matching the game's native UI look (IMGUI is plainer by design, §06).

## Stack
- C# class library `ShelfTransfer`, net6.0, built with the installed .NET 10 SDK (targeting pack).
  No .NET 6 SDK needed.
- BepInEx 6 IL2CPP, pinned to build **be.755**, manual install. HarmonyX bundled, **at most one patch**
  (read-only, on the aim target, only if it cannot be read directly).
- Game access through the `BepInEx/interop` assemblies generated on first launch (Il2CppInterop).
- UI: Unity IMGUI from an injected MonoBehaviour. No asset bundles.
- Config: BepInEx `.cfg` (keybinds, items per frame).
- Inspection: UnityExplorer_Sons (BepInEx IL2CPP build).
- Post-build copy of the DLL to `<game>/BepInEx/plugins`.
- Game folder: `C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest`.
- Repo: private GitHub `Prerendered/sotfMod` (empty at intake).

Two projects, split by what can run without the game:
- **ShelfTransfer.Core**: no BepInEx and no interop references. It holds the transfer state
  machine (against a game-gateway interface), amount clamping, shelf key, grouping and sorting,
  bearing and distance formatting, the label store and the log formatter. Unit-tested.
- **ShelfTransfer.Plugin**: the BepInEx plugin, the interop adapter that implements the gateway,
  IMGUI and the Harmony patch. References game-generated assemblies, so it builds only on
  User's machine.

**Monorepo layout (User request).** One repo, one solution, laid out so later SotF mods slot in
beside this one:
```
sotfMod/
  SotfMods.sln
  Directory.Build.props        # net6.0, GamePath (set once), BepInEx/interop refs for plugin projects
  mods/
    ShelfTransfer/
      ShelfTransfer.Core/        # no game refs
      ShelfTransfer.Plugin/      # BepInEx plugin, interop adapter, IMGUI
      ShelfTransfer.Core.Tests/
  shared/                      # empty until a second mod needs common code (no speculative shared lib)
  team/                        # plan, architecture, board, reports
```
`GamePath` is set once in `Directory.Build.props`, overridable through a git-ignored
`Directory.Build.local.props`, so no machine path is committed. Ships as two DLLs
(`ShelfTransfer.dll` + `ShelfTransfer.Core.dll`) copied to `BepInEx/plugins`. No IL merge.

CI (GitHub Actions) builds and tests **Core only**. Game-derived interop assemblies are never
committed. Plugin build correctness is verified locally (exit-0 `dotnet build` against the real
interop folder).

## Design direction
A light grey panel with a violet accent, per the brief's mockup (§06, Figure 4). The mockup is the
design spec, so no designer dispatch is needed.

## Decisions
- **D1. Loader.** BepInEx be.755 over RedLoader (§08), adopted as written.
- **D2. Shelf identity.** Type name + world position rounded to 0.1 m behind a single
  `GetShelfKey()`, swapped for a persistent save ID if step 1 finds one (§05, §07).
- **D3. Carry cap (amends the brief).** The brief says per-item carry caps never apply because
  an item spends one frame in inventory. Caps are most likely checked at take time, not over time.
  Step 2 tests a transfer while the player is at the carry cap for that item. If the take is
  refused, the overlay blocks the transfer with "free one slot of <item> first" (invariant 4).
  If the take succeeds, D3 is closed as a non-issue.
- **D4. Disconnect wording (amends the brief).** A disconnect between a take and a store leaves
  one item in the client's inventory. That item is not lost: the state is some moved, at most
  one in inventory, the rest on the source.
- **D5. Mod log file (User request).** The plugin writes its own log at
  `<game>/BepInEx/ShelfTransfer/shelftransfer.log`, separate from BepInEx's `LogOutput.log`.
  - Each session starts a fresh file. The previous session's file is kept as `shelftransfer.prev.log`.
  - One line per event: ISO timestamp, level, event name, then `key=value` fields, so lines grep cleanly.
  - Events logged: plugin load and versions; discovery results (classes, fields and methods
    found or missing); aim target changes (debounced); transfer start (source key, target key,
    item id, amount, starting counts); every take and store with counts before and after and
    the verification result; the stop reason; label store reads and writes; every exception
    with its stack trace.
  - Claude reads this file and `BepInEx/LogOutput.log` directly after each in-game test.
    Combined with User's report, it is the evidence for the end-to-end gate.
- **D6. Discovery dump (aid for step 1).** A config-gated debug hotkey (off by default) writes the
  aimed GameObject's components, fields and methods to the mod log. This cuts the manual
  UnityExplorer transcription in step 1. UnityExplorer stays in the stack for live poking.
- **D7. Esc during a running transfer** stops after the in-flight item's store completes, never
  between a take and its store (invariants 1 and 3).
- **D8. Pre-flight RedLoader check (amends the brief).** Only `version.dll` and `_RedLoader`
  indicate RedLoader. BepInEx IL2CPP also ships `dotnet/` and `doorstop_config.ini`. Checked at
  intake: the game folder is clean.
- **D9. Process cadence.** Claude cannot run the game. Every card's end-to-end gate is an in-game
  test User performs, backed by the mod log. Undriveable legs are reported as
  `manual test — confirm`, never as passed. **Step 2 is a go/no-go gate**: no UI card starts until
  User confirms the host saw the transferred items. If step 2 fails, re-plan (host companion or
  single-player scope) with User.
- **D10. Testing split.** Core is covered by real unit tests run by the tester, including the
  transfer state machine against a fake gateway that simulates a refused store, a take that
  doesn't decrement, a source changed by another player, an exception mid-tick, and Esc mid-run.
  Plugin legs are covered by the brief's §09 test matrix, run by User in game.

## Build order (from §09, amended)
0. **Pre-flight (User).** Steam set to "update only on launch". Install BepInEx be.755, launch once
   (`BepInEx/interop` exists, `LogOutput.log` shows BepInEx loaded), add UnityExplorer_Sons.
   Make a throwaway single-player save. Line up a friend for steps 2 and 6.
1. **Setup + discovery.** Scaffold the solution, mod log (D5) and discovery dump (D6). User records
   the shelf storage class, slot layout, item id and count fields, and the take/store methods.
   Answers go into open questions.
2. **Console command `transfer <count>`.** Go/no-go on a friend-hosted session, including a
   30 m distant target and the carry-cap case (D3).
3. **Label and group system.** Independent of step 2's outcome. Can run in parallel with step 2.
4. **Transfer hint.** "L — Transfer" drawn next to the game's E and C prompts.
5. **Overlay.** Grouped list with fit counts, amount control, confirm, world marker, result panel.
6. **Acceptance test.** The §09 matrix on a friend-hosted session, including both players reloading the save.

## Invariant check (run at plan time)
| Flow | Invariant at risk | Result |
|---|---|---|
| Take 1, store 1, verify, repeat | 1, 2, 3 | Holds. Each pair is verified before the next |
| Target fills mid-transfer | 1 | Holds. One item stranded in inventory, reported |
| Source changed by another player mid-run | 1, 3 | Holds. Count mismatch triggers stop and report |
| Disconnect mid-transfer | 1 | Holds after D4 rewording (brief claimed no inventory residue) |
| Player at carry cap | 4 | **Contradiction in the brief.** Resolved by D3 (tested in step 2, blocked in overlay) |
| Esc while overlay open | 1 | Holds. Nothing moves |
| Esc while transfer running | 1, 3 | Unspecified in the brief. Resolved by D7 |
| Incompatible target row | 4 | Holds. Dimmed, unselectable |
| Amount > target capacity | 4 | Holds. Clamped before confirm |
| Label / group edits, world text | 5 | Holds. JSON only, no game writes |
| Discovery dump (D6) | 2 | Holds. Read-only reflection, no calls with side effects |

No flow contradicts an invariant after D3, D4 and D7.

## Open questions (resolved at intake)
- Repo and CI: private GitHub `Prerendered/sotfMod`. CI builds and tests Core only.
- Test cadence: in-game test per card by User, plus the mod log Claude can read (D5, D9).

## Open questions (answered by step 1/2 discovery, not blocking the plan)
- Exact storage component class and take/store method names.
- Does the game expose the aimed slot's row and column directly, or only the item?
- Is there a persistent per-structure save ID readable from the client? (D2)
- What identifies the current world from the client, for the labels file name? On a friend's
  session the host's save id may not be readable; fall back to host name + slot or session id.
- Does the host apply a store call for a shelf 30 m away? (go/no-go)
- Does the take call respect the carry cap? (D3)
