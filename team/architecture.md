<!-- owner: architect · readers: dev, reviewer, tester, seer -->

# Architecture — Shelf Transfer Mod (Sons of the Forest)

Client-only BepInEx 6 IL2CPP plugin. Source of truth for scope and decisions: `team/plan.md`
(D1–D10) and the brief (`Shelf-Transfer-Mod-Feature-Doc.pdf`, cited as §NN). This file says how
the code is shaped so every story lands in a known place.

## Invariants

Carried verbatim from plan.md. The right column is where each is enforced; a reviewer checks the
diff against it, the Overseer checks every new flow against the left column.

| # | Invariant (verbatim) | Enforced in |
|---|---|---|
| 1 | **Conservation.** No item is ever duplicated or lost. At every step of a transfer, source + target + (at most 1 in the player's inventory) = the starting total. If an item is stranded in inventory (store refused, disconnect), the mod reports it. It never retries blindly. | `Core/Transfer/TransferRunner` (story): one take then one store per cycle, counts re-read after each; `TransferReport.StrandedInInventory` is 0 or 1 and comes from re-reading inventory, never from assumption. No code path calls `TakeOne` twice without a `StoreOne` and verify between. `transfer.stop` log line carries `s0 moved remaining stranded` so conservation is auditable from the log. |
| 2 | **Vanilla path only.** Shelf state changes only through the game's own take and store calls, the same calls a player triggers with E and C. No direct writes to storage state, no host-side code, no custom network messages. | `IShelfGateway` is the only type that touches storage, and it exposes exactly `TakeOne` / `StoreOne` plus reads. `Plugin/Game/InteropShelfGateway` (story) implements them by invoking the discovered take/store methods. Core has no interop reference (csproj comment + CI builds Core without the game). Harmony: at most one patch, read-only, on the aim target (P7). |
| 3 | **Verify before next.** No take starts until the previous take and store are confirmed by re-reading counts. Any mismatch, refusal or exception stops the loop and produces a report. | `TransferState` has no edge from `TakenAwaitingVerify` or `StoredAwaitingVerify` back to `ReadyForNext` except through a count check (flow F2). Any `GatewayOutcome` other than `Ok`, or any mismatch, goes to `Finished` with a `StopReason`. The driver's tick is wrapped in try/catch that stops the runner (P2). |
| 4 | **Never start what can't finish.** The amount is clamped to min(source stack, target free capacity). A target that cannot hold the item type cannot be selected. A transfer the player cannot physically perform (at carry cap, see D3) is blocked before it starts. | `TransferRunner.Planning` clamps `Amount` to `min(request.Amount, GetCount(source), GetFreeCapacity(target))` and finishes `BlockedBeforeStart` when 0 or `!CanHold`. Overlay (story) dims rows with `CanHold == false` and clamps the stepper with the same Core function so UI and runner cannot disagree. D3: if step 2 shows a take refused at carry cap, `IShelfGateway` gains `CanTakeOne` and Planning checks it. |
| 5 | **Labels are local.** Labels and groups live only in a client-side JSON file. They never touch game state or the network. | `Core/Labels` depends on nothing in `Core/Gateway` except the shelf key string. `Plugin/Labels/JsonLabelFileSystem` (story) writes only under `BepInEx/config/ShelfTransfer/`. No label type is reachable from `InteropShelfGateway`. |

## Modules and boundaries

Six modules from §04 plus the two plan additions (D5 log, D6 discovery). Imports go one way:
Plugin → Core, never back; inside Plugin, `Game/` and `Ui/` never reference each other directly —
they meet in `ShelfTransferPlugin` and the runtime driver.

| Module | Lives in | Depends on | Reads game | Writes game |
|---|---|---|---|---|
| Aim watcher | `Plugin/Game/AimWatcher` | interop aim target, Harmony (P7, only if needed) | yes | no |
| Shelf registry | `Plugin/Game/ShelfRegistry` → `Core/Shelves` (grouping, sorting, bearing) | interop storage components, `ShelfKey` | yes | no |
| Transfer runner | `Core/Transfer/TransferRunner` driven by `Plugin/Runtime/TransferDriver` | `IShelfGateway` | via gateway | via gateway only |
| Game gateway | `Plugin/Game/InteropShelfGateway` implements `Core/Gateway/IShelfGateway` | interop take/store methods | yes | the only writer |
| UI | `Plugin/Ui/*` (IMGUI MonoBehaviours: hint, overlay, label editor, world labels, marker) | Core view models, Config | no (cursor/input only) | no |
| Label store | `Core/Labels/LabelStore` + `Plugin/Labels/JsonLabelFileSystem` | file system | no | no |
| Config | `Plugin/Config/ModConfig` | BepInEx `ConfigFile` | no | no |
| Mod log (D5) | `Core/Logging/IModLog`, `LogLine` + `Plugin/Logging/FileModLog` | file system | no | no |
| Discovery dump (D6) | `Plugin/Discovery/DiscoveryDump` | interop reflection, `IModLog`, Config | yes (read-only reflection) | no |

### The game-gateway interface

Core's only view of the game. Defined in `mods/ShelfTransfer/ShelfTransfer.Core/Gateway/IShelfGateway.cs`:

```csharp
public interface IShelfGateway
{
    bool IsAlive(ShelfRef shelf);                       // null/destroyed check, before every call
    int GetCount(ShelfRef shelf, ItemId item);
    int GetFreeCapacity(ShelfRef shelf, ItemId item);   // 0 when the type is not accepted
    bool CanHold(ShelfRef shelf, ItemId item);
    int GetInventoryCount(ItemId item);
    GatewayOutcome TakeOne(ShelfRef shelf, SlotRef slot, ItemId item);   // what E does
    GatewayOutcome StoreOne(ShelfRef shelf, ItemId item);                // what C does
}
```

Supporting records: `ShelfRef(Key, TypeName, Position)`, `SlotRef(Row?, Column?)`, `ItemId(int)`,
`WorldPosition(X, Y, Z)`, `AimTarget(Shelf, Slot, Item, Count)`, `GatewayOutcome(Status, Detail)`
with `Ok | Refused | Failed`. The plugin keeps a `Dictionary<string, Il2Cpp component>` from
`ShelfRef.Key` to the live object; Core never holds a game reference.

The step 1 discovery answers (storage class, slot fields, take/store method names) land only in
`InteropShelfGateway`, `AimWatcher` and `ShelfRegistry`. If the game patches, those three files
change and nothing in Core moves.

## Flows

### F1 — Aim to transfer (request flow across modules)

```
 every frame                                        on L (Config.TransferKey)
 ┌─────────────┐  AimTarget?   ┌────────────┐  open   ┌──────────────────────┐
 │ AimWatcher  │─────────────▶│ HintUi     │────────▶│ OverlayUi            │
 │ (Game)      │  null → hide  │ "L—Transfer"│         │ rows = ShelfListBuilder(│
 └─────────────┘               └────────────┘         │   registry.List(),      │
        │ reads aim target / Harmony postfix           │   labelStore, player,   │
        ▼                                              │   gateway.CanHold/Free) │
   game aim target                                     └──────────┬───────────┘
                                                                  │ Confirm(target, amount)
                                                                  ▼
 ┌──────────────────────┐  Tick() per frame  ┌──────────────────────────────┐
 │ TransferDriver       │──────────────────▶│ TransferRunner (Core, pure)  │
 │ (Plugin/Runtime,     │  ◀── state/report  │ Planning → cycles → Finished │
 │  MonoBehaviour.Update│                    └───────────┬──────────────────┘
 │  try/catch boundary) │                                │ IShelfGateway calls
 └──────────┬───────────┘                                ▼
            │ report                          ┌──────────────────────────┐
            ▼                                 │ InteropShelfGateway      │──▶ game take/store
   OverlayUi result panel                     │ (null/destroyed checks)  │◀── counts
   + FileModLog transfer.* lines              └──────────────────────────┘
```

### F2 — Transfer state machine (one item type, one cycle per item)

A cycle is take → verify → store → verify and runs to completion inside one `Tick()`; the driver
never yields between a take and its store, which is how D7 holds structurally. `ItemsPerFrame`
(config, default 1) is the number of cycles one tick may run.

```
                 ┌──────────┐ clamp n = min(Amount, GetCount(src), GetFreeCapacity(dst))
   Start ───────▶│ Planning │ !IsAlive(src|dst) ─────────────────────▶ Finished(ShelfGone)
                 └────┬─────┘ !CanHold(dst) or n == 0 ───────────────▶ Finished(BlockedBeforeStart)
                      │ record S0 = GetCount(src), I0 = GetInventoryCount
                      ▼
            ┌──────────────────┐ moved == n ──────────────────────────▶ Finished(Completed)
   ┌───────▶│ ReadyForNext     │ cancelRequested (Esc, D7) ───────────▶ Finished(Cancelled)
   │        └────────┬─────────┘ !IsAlive(src|dst) ───────────────────▶ Finished(ShelfGone)
   │                 │ s = GetCount(src); i = Inv; TakeOne(src, slot, item)
   │                 │   Refused ──────────────────────────────────────▶ Finished(TakeRefused)
   │                 │   Failed ───────────────────────────────────────▶ Finished(GatewayError)
   │                 ▼
   │        ┌────────────────────┐ GetCount(src) != s-1 ─────────────▶ Finished(SourceMismatch)
   │        │ TakenAwaitingVerify│ Inv != i+1 ─────────────────────────▶ Finished(InventoryMismatch)
   │        └────────┬───────────┘
   │                 │ t = GetCount(dst); StoreOne(dst, item)
   │                 │   Refused (target filled mid-run) ──────────────▶ Finished(StoreRefused)   stranded=1
   │                 │   Failed ───────────────────────────────────────▶ Finished(GatewayError)
   │                 ▼
   │        ┌─────────────────────┐ GetCount(dst) != t+1 ─────────────▶ Finished(TargetMismatch)
   │        │ StoredAwaitingVerify│ Inv != i ───────────────────────────▶ Finished(InventoryMismatch)
   │        └────────┬────────────┘
   │                 │ moved++
   └─────────────────┘

   Finished(reason): report = (reason, Requested=n, Moved, Remaining=GetCount(src) re-read,
                     StrandedInInventory=clamp(Inv - I0, 0, 1) re-read, Detail)
                     log transfer.stop; if S0 != Remaining + Moved + Stranded → log conservation.mismatch (WARN)
```

Stop reasons and their player-facing meaning live in `Core/Transfer/StopReason.cs`. A disconnect
(D4) surfaces as `GatewayError` or `ShelfGone` with the re-read stranded count — the report says
"n moved, 1 in your inventory, rest on the source", never "lost".

### F3 — Labels (invariant 5)

```
  look at shelf + LabelKey        overlay row "rename" / "group"
            │                                │
            ▼                                ▼
   ┌─────────────────┐  key = GetShelfKey(type, pos)  ┌────────────────────┐
   │ LabelEditorUi   │──────────────────────────────▶│ LabelStore (Core)  │
   │ (IMGUI text box)│   Set(key, label, group?)      │ in-memory map      │
   └─────────────────┘                                └───────┬────────────┘
                                                              │ Save() → LabelFile (DTO)
                                                              ▼
                                                 ┌─────────────────────────────┐
                                                 │ JsonLabelFileSystem (Plugin)│
                                                 │ System.Text.Json, write to  │
                                                 │ temp then File.Move (atomic)│
                                                 └──────────────┬──────────────┘
                                                                ▼
                                   BepInEx/config/ShelfTransfer/<worldId>.json
   Readers: OverlayUi rows (label or "Shelf · 38 m NE"), WorldLabelUi (text above shelf when near).
   Prune: on overlay open, keys the registry no longer finds are dropped from the file (§07).
```

Label file schema (`Core/Labels/LabelFile.cs`, version 1):

```json
{
  "version": 1,
  "worldId": "host-Alice_slot2",
  "shelves": {
    "StorageShelf@12.3,4.5,-6.7": { "label": "Ammo", "group": "Main base" },
    "StorageShelf@40.0,4.5,-6.7": { "label": "Food",  "group": null }
  }
}
```

Groups are derived from the set of non-null `group` values; a group with no shelves does not
exist (YAGNI, §07 "new group names are created inline" is satisfied by assigning as you create).
`worldId` is the save's own id if readable, else host name + slot (plan open question); it is
sanitised to `[A-Za-z0-9_-]` before use as a file name.

### F4 — Mod log and discovery (D5, D6)

```
 Core (IModLog.Write / WriteException)        Plugin
 ──────────────────────────────────▶  FileModLog ──▶ <game>/BepInEx/ShelfTransfer/shelftransfer.log
 BepInEx Log (console/LogOutput.log)   on start: rename existing → shelftransfer.prev.log, open fresh
 stays for load/fatal lines only
 DiscoveryDump (Config.DiscoveryKey, off by default): aimed GameObject → components → fields, methods
   one discovery.* line each, read-only reflection, no method invoked.
```

Log line format (one event per line, grep-friendly):

```
<ISO-8601 local with offset> <LEVEL padded 5> <event> <key>=<value> <key>=<value> ...

2026-10-18T14:35:02.123+02:00 INFO  plugin.load version=0.1.0 core=0.1.0 bepinex=6.0.0-be.755
2026-10-18T14:36:10.004+02:00 INFO  transfer.start source="StorageShelf@12.3,4.5,-6.7" target="StorageShelf@40.0,4.5,-6.7" item=312 amount=6 s0=6 t0=0 i0=0
2026-10-18T14:36:10.021+02:00 INFO  transfer.take  n=1 src_before=6 src_after=5 inv_before=0 inv_after=1 verify=ok
2026-10-18T14:36:10.022+02:00 INFO  transfer.store n=1 dst_before=0 dst_after=1 inv_before=1 inv_after=0 verify=ok
2026-10-18T14:36:10.140+02:00 WARN  transfer.stop  reason=StoreRefused moved=4 remaining=1 stranded=1 detail="target full"
2026-10-18T14:36:10.141+02:00 ERROR error event=transfer.tick type=NullReferenceException message="..."
    at ShelfTransfer.Plugin.Game.InteropShelfGateway.StoreOne(...)        <- stack trace lines indented 4 spaces
```

Rules: keys match `[a-z0-9_.]+`; values are quoted with `"` when they contain a space, `=` or
`"` (inner quotes escaped `\"`); numbers and enums unquoted; event names are
`<module>.<verb>` lowercase. Events in the catalogue (D5): `plugin.load`, `discovery.component`,
`discovery.member`, `discovery.missing`, `aim.change` (debounced to on-change), `transfer.start`,
`transfer.take`, `transfer.store`, `transfer.stop`, `conservation.mismatch`, `labels.read`,
`labels.write`, `error`. The formatter (`Core/Logging/LogLine`, story) is pure and unit-tested;
`FileModLog` only appends and flushes.

## Patterns

### P1 — Gateway adapter: every interop call guarded, outcomes as values

```csharp
// Plugin/Game/InteropShelfGateway.cs (story)
public sealed class InteropShelfGateway : IShelfGateway
{
    private readonly Dictionary<string, StorageComponent> _byKey = new();   // interop type from step 1

    public bool IsAlive(ShelfRef shelf) =>
        _byKey.TryGetValue(shelf.Key, out StorageComponent? c) && c != null && c.gameObject != null;

    public GatewayOutcome StoreOne(ShelfRef shelf, ItemId item)
    {
        if (!_byKey.TryGetValue(shelf.Key, out StorageComponent? c) || c == null)
        {
            return GatewayOutcome.Failed("shelf gone");
        }
        try
        {
            return c.TryStore(item.Value, 1) ? GatewayOutcome.Ok : GatewayOutcome.Refused("store declined");
        }
        catch (Exception ex)   // interop can throw on collected objects; the runner must see a value
        {
            return GatewayOutcome.Failed(ex.GetType().Name + ": " + ex.Message);
        }
    }
}
```

Rules:
- `IsAlive` or an equivalent null/destroyed check before every interop call (§11 risk).
- Gateway methods never throw; they return `Failed`. try/catch is allowed here because it is the
  Core/game boundary (standards §8).
- Only `TakeOne` and `StoreOne` call anything with side effects. Adding another mutating method
  is an architecture change, not a story detail.
- Il2Cpp collections are copied into managed lists before iteration.

### P2 — Tick driver: the per-frame boundary owns try/catch, the runner stays pure

```csharp
// Plugin/Runtime/TransferDriver.cs (story) — a MonoBehaviour, see P3 for the IL2CPP shape
public void Update()
{
    if (_runner is null || _runner.State == TransferState.Finished) { return; }
    try
    {
        _runner.Tick(_config.ItemsPerFrame.Value);
    }
    catch (Exception ex)
    {
        _log.WriteException("transfer.tick", ex);
        _runner.Abort(StopReason.GatewayError, ex.Message);   // produces the report, nothing retried
    }
    if (_runner.State == TransferState.Finished) { _onFinished(_runner.Report!); }
}
```

Rules:
- `TransferRunner.Tick` is synchronous and deterministic; tests drive it with a fake gateway (D10).
- Cancel is `RequestCancel()`; the runner honours it only in `ReadyForNext` (D7).
- One runner at a time; the overlay cannot start a second while one is running.

### P3 — Injected MonoBehaviour (IL2CPP shape)

```csharp
// Plugin/Ui/OverlayUi.cs (story)
public sealed class OverlayUi : MonoBehaviour
{
    public OverlayUi(IntPtr ptr) : base(ptr) { }   // required by Il2CppInterop for injected types

    public void OnGUI() { /* IMGUI only; no business logic, calls Core view-model builders */ }
}

// ShelfTransferPlugin.Load():
OverlayUi overlay = AddComponent<OverlayUi>();   // registers the type with Il2Cpp if needed
```

Rules:
- Every injected type has the `IntPtr` constructor and is created through `BasePlugin.AddComponent<T>()`
  [ref: BepInEx.Unity.IL2CPP.xml, `BasePlugin.AddComponent` "Automatically registers the type"].
- Unity messages (`Update`, `OnGUI`) are public methods. No fields of Il2Cpp types stored across frames
  without an `IsAlive`-style check before use.
- UI classes hold no game references; they receive view models built in Core and raise C# events.

### P4 — Outcomes and reports, not exceptions

Core returns `GatewayOutcome` / `TransferReport` / `StopReason`; it throws only on programmer error
(`ArgumentException` for a blank type name). Plugin catches at two boundaries only: gateway
methods (P1) and the per-frame tick (P2). Nested try/catch is a review fail.

### P5 — Config

```csharp
// Plugin/Config/ModConfig.cs (story)
public sealed class ModConfig
{
    public ConfigEntry<KeyCode> TransferKey { get; }
    public ConfigEntry<KeyCode> LabelKey { get; }
    public ConfigEntry<int> ItemsPerFrame { get; }          // 1..5, default 1
    public ConfigEntry<bool> DiscoveryEnabled { get; }      // D6 gate, default false
    public ConfigEntry<KeyCode> DiscoveryKey { get; }

    public ModConfig(ConfigFile file)
    {
        TransferKey = file.Bind("Keys", "Transfer", KeyCode.L, "Opens the transfer overlay when aiming at a shelf slot");
        // ...
    }
}
```

Rules: all `Bind` calls in this one class; sections `Keys`, `Transfer`, `Debug`; every entry has a
description; values are read through the entry each time (no caching) so edits apply live.

### P7 — Harmony: at most one patch, read-only

Only if the aim target cannot be read directly. It lives in `Plugin/Game/AimPatch.cs`, is a
`Postfix` that copies values into `AimWatcher`, never changes arguments or return values, and is
applied with `Harmony.CreateAndPatchAll(typeof(AimPatch))` from `Load()`. A second patch needs a
Decisions line and Overseer sign-off.

### Conventions for this stack (standards §0 equivalents)

- Naming: PascalCase types/methods/properties, camelCase locals/parameters, `_camelCase` private
  fields, `SCREAMING_CASE` only for `const` in `PluginInfo`. One type per file, file = type name,
  folder = namespace. Booleans `Is/Has/Can/Should` prefixed.
- File header: `// purpose:` `// owns:` `// deps:` (+ optional `// caution:`) as the first lines.
- Explicit `using`s (ImplicitUsings off), grouped System → third-party → `ShelfTransfer.*`, blank line between groups.
- Lint/format: `.editorconfig` + `EnforceCodeStyleInBuild`; `dotnet format --verify-no-changes SotfMods.sln` is the lint command. Core treats warnings as errors.
- Tests: xUnit, separate project `ShelfTransfer.Core.Tests` mirroring Core's folders; class `<Type>Tests`, method `Method_Scenario_Expectation`. Fakes live in `ShelfTransfer.Core.Tests/Fakes/`. Plugin has no unit tests (game-bound); its evidence is the mod log + User's in-game run (D9).
- Commands: `dotnet build mods/ShelfTransfer/ShelfTransfer.Core` · `dotnet test mods/ShelfTransfer/ShelfTransfer.Core.Tests` · `dotnet build mods/ShelfTransfer/ShelfTransfer.Plugin` (local only, needs `BepInEx/interop`; deploys to `<GamePath>/BepInEx/plugins/ShelfTransfer/`).

## Structure

As scaffolded on disk (git-tracked files only; `bin/`, `obj/` and `Directory.Build.local.props` are ignored).

```
sotfMod/
  .claude/                                     teamclaude agents + skills (incl. reference-library.md)
  .editorconfig
  .gitattributes                               LF everywhere (sln CRLF), pdf binary
  .gitignore                                   bin/obj, local props, *.dll/*.pdb, BepInEx/, interop/
  CLAUDE.md
  Directory.Build.local.props.example          copy → Directory.Build.local.props to override GamePath
  Directory.Build.props                        net6.0, nullable, analyzers, IsBepInExPlugin=false, GamePath default
  Directory.Build.targets                      plugin-only: BepInEx/core + interop refs (Private=false), interop check, deploy copy
  Shelf-Transfer-Mod-Feature-Doc.pdf
  SotfMods.sln
  global.json                                  SDK 10.0.201, rollForward latestFeature
  mods/
    ShelfTransfer/
      ShelfTransfer.Core/                      no BepInEx / interop / UnityEngine references, ever
        ShelfTransfer.Core.csproj
        Gateway/
          AimTarget.cs
          GatewayOutcome.cs
          IShelfGateway.cs
          ItemId.cs
          ShelfRef.cs
          SlotRef.cs
          WorldPosition.cs
        Labels/
          LabelFile.cs
          ShelfLabel.cs
        Logging/
          IModLog.cs
          LogLevel.cs
        Shelves/
          ShelfKey.cs
        Transfer/
          StopReason.cs
          TransferReport.cs
          TransferRequest.cs
          TransferState.cs
      ShelfTransfer.Core.Tests/                xunit, net10.0, mirrors Core folders
        ShelfTransfer.Core.Tests.csproj
        Shelves/
          ShelfKeyTests.cs
      ShelfTransfer.Plugin/                    AssemblyName ShelfTransfer → ShelfTransfer.dll
        ShelfTransfer.Plugin.csproj
        PluginInfo.cs
        ShelfTransferPlugin.cs
  shared/
    README.md
  team/
    architecture.md  board.md  design.md  map.md  plan.md  review-report.md  security-report.md  state.md  test-report.md
```

Folders stories add (module → folder; create on first use, do not pre-create empties):

| Story area | Core | Plugin | Tests |
|---|---|---|---|
| Transfer runner | `Transfer/TransferRunner.cs`, `Transfer/AmountClamp.cs` | `Runtime/TransferDriver.cs` | `Transfer/TransferRunnerTests.cs`, `Fakes/FakeShelfGateway.cs` |
| Shelf list, bearing | `Shelves/ShelfListBuilder.cs`, `Shelves/Bearing.cs`, `Shelves/ShelfRow.cs` | `Game/ShelfRegistry.cs`, `Game/InteropShelfGateway.cs`, `Game/AimWatcher.cs`, (`Game/AimPatch.cs`) | `Shelves/*Tests.cs` |
| Labels | `Labels/LabelStore.cs`, `Labels/ILabelFileSystem.cs`, `Labels/WorldId.cs` | `Labels/JsonLabelFileSystem.cs` | `Labels/LabelStoreTests.cs` |
| Mod log | `Logging/LogLine.cs` | `Logging/FileModLog.cs` | `Logging/LogLineTests.cs` |
| Config, discovery | — | `Config/ModConfig.cs`, `Discovery/DiscoveryDump.cs` | — |
| UI | view models in `Shelves/` | `Ui/HintUi.cs`, `Ui/OverlayUi.cs`, `Ui/LabelEditorUi.cs`, `Ui/WorldLabelUi.cs`, `Ui/ShelfMarker.cs`, `Ui/Styles.cs` | — |

## Decisions

- chose local assembly references from `$(GamePath)/BepInEx/{core,interop}` over the `BepInEx.Unity.IL2CPP` NuGet feed (TheOtherRoles pattern) because the mod must bind to the exact installed be.755 and to interop generated from the installed game build; no NuGet package exists for SotF interop. [ref: plan.md Stack; TheOtherRolesAU/TheOtherRoles csproj]
- chose `Directory.Build.targets` for the plugin references and deploy step over putting them in `ShelfTransfer.Plugin.csproj` because `IsBepInExPlugin` set in a csproj is only visible after project evaluation, and a second mod gets the same wiring by setting one property. Deviation from plan.md's layout (which lists only `Directory.Build.props`), reason recorded here. [ref: MSBuild evaluation order]
- chose a default Steam `GamePath` in `Directory.Build.props` with a git-ignored `Directory.Build.local.props` override over an empty default, because the default Steam path is not machine-specific and makes the common case build with zero setup. [ref: plan.md "GamePath set once"]
- chose `net10.0` for `ShelfTransfer.Core.Tests` over `net6.0` because no .NET 6 runtime is installed here or on a stock CI runner (`dotnet --list-runtimes`: 8.0.11, 10.0.5) and a newer test host loads a net6.0 library; Core stays net6.0 for the game's CoreCLR. [ref: dotnet --list-runtimes]
- chose blanket `*.dll` / `*.pdb` in `.gitignore` plus `Private=false` on every game reference over a curated list, because the repo ships source only and this makes committing a game-derived assembly impossible by accident. [ref: plan.md CI section]
- chose an engine-free `WorldPosition` record over `UnityEngine.Vector3` in Core because Core must build without `UnityEngine.CoreModule`; the plugin converts at the gateway. [ref: plan.md Stack "no interop references"]
- chose `GatewayOutcome` values (`Ok | Refused | Failed`) over exceptions from the gateway because the runner needs to distinguish "game said no" (nothing moved) from "something broke" (re-read counts), and standards §8 forbids try/catch in the hot loop. [ref: standards §8]
- chose System.Text.Json for the label file over Newtonsoft because it ships in the game's `dotnet/` CoreCLR folder (`System.Text.Json.dll` present), so no extra DLL is deployed. [ref: `<game>/dotnet/System.Text.Json.dll`]
- chose to implement `ShelfKey.GetShelfKey` and three pinning tests in the scaffold over leaving Core empty because D2 is architecture (the one swap point for shelf identity) and the test project needs one real test to prove the Core ↔ Tests wiring; the tester owns all further tests. [ref: plan.md D2, D10]
- chose the classic `.sln` (`dotnet new sln -f sln`) over SDK 10's default `.slnx` because plan.md names `SotfMods.sln` and tooling support for `.slnx` is still uneven. [ref: plan.md Monorepo layout]
- chose `GenerateDocumentationFile=true` + `NoWarn CS1591` over disabling IDE0005, because unused-using enforcement on build is the lint we want and per-member XML docs duplicate the file headers. [ref: roslyn#41640 build error text]
- chose BepInEx docs + TheOtherRoles + UnityExplorer as the three references (budget), appended to `.claude/skills/reference-library.md` under `bepinex-il2cpp` as unvetted. [ref: senior-protocol rule 2]
