# DVRouteManager — Debug Branch

> **This is the active development branch. Code here is unfinished and may be unstable.**
> For the stable release see the [`master` branch](https://github.com/odwales20/DVRouteManager/tree/master).

A [Derail Valley](https://store.steampowered.com/app/588030/Derail_Valley/) mod that adds route management, automatic junction switching, cruise control, and autonomous AI driving via the Comms Radio.

> **Forked from [WallyCZ/DVRouteManager](https://github.com/WallyCZ/DVRouteManager)** — original mod by Wally.

---

## What's Different on This Branch

The `Debug` branch contains the in-progress **AI speed limit overhaul**. All other changes (UI fixes, routing, other locos) are made on `master` and then merged here.

### AI Speed Limit System (WIP)

The new system mirrors the game's own `SignPlacer.GetTrackSigns` pipeline exactly rather than trying to approximate it:

- `BezierArcApproximation` (error=1f) computes curve arcs for each track
- `SignPlacerUtils.ChunkifyNumbers(300f)` groups arcs into 300 m segments
- `SignPlacerUtils.MinimizeSpeedDifference(30f, 300f)` raises the speed of short segments that drop too steeply (same parameters the game uses)
- Junction end cap: last segment capped to 60 km/h when a track leads into a junction
- **Per-segment, position-aware lookahead**: a tight section 1500 m into a long track does not slow the AI until it is actually within braking distance of that section — fixes the old "sitting at 60 for an entire track" problem
- Yard speed cap: `[Y]` tracks are limited to 50 km/h (these are excluded from sign placement but still need a sensible limit)
- DE4 startup fix: throttle capped at 25 % below 5 km/h to prevent traction motor overload from standstill

### Known Remaining Issues

- Speed limiting on `Road`-prefixed tracks is still being tuned — the game removes signs from these tracks via `noSignsTrackNameMarks` (not readable at runtime), so our geometry-based limits may be slightly conservative on some road sections
- Current-track speed limit uses the whole-track minimum rather than the AI's actual position within the track; the AI may slow slightly earlier than necessary on long tracks with a tight section near one end

---

## Features

### Route Planning & Navigation
- **A\* pathfinding** over the full RailTrack graph
- **Automatic junction switching**
- **Turntable routing** — auto-rotates turntables to the correct heading
- **Yard track avoidance** — penalises occupied or reserved sidings
- **Flip direction** — reverse the active route in-place
- **Map markers** — route drawn on the in-game map

### Cruise Control
- **PID speed controller**
- **DM3 automatic gear shifting** — up at >800 RPM, down at <600 RPM, 70 km/h cap
- **Steam loco support (S060 / S282)** — pressure-aware cutoff, pulse braking
- **Overheat protection** — throttle backed off at Warning, forced down at Critical

### Autonomous AI Driver
- **Drive to destination** — set a destination via Comms Radio; AI drives there automatically
- **Freight haul automation** — 4-phase: route to cars → couple → drive → deliver
- **Speed limit lookahead** — geometry-based per-segment limits (see above)
- **Turntable awareness** — stops and waits for turntable to finish rotating
- **Competing mod safety** — disables DriverAssist and SteamCruiseControl on start

### Comms Radio UI
- New Route / Active Route / Loco AI / Settings

---

## Status

| Area | Status |
|------|--------|
| Build | ✅ Compiles |
| Comms Radio UI | ✅ Working |
| Route pathfinding | ✅ Working |
| Junction switching | ✅ Working |
| Route tracking | ✅ Working |
| Turntable routing | ✅ Working |
| Cruise control | ✅ Working |
| Freight haul AI | ✅ Working |
| Map markers | ✅ Working |
| **AI speed limits** | 🚧 In progress — tuning ongoing |
| Audio cues | ❌ Not working |

---

## Requirements

- [Derail Valley](https://store.steampowered.com/app/588030/Derail_Valley/) (current build)
- [Unity Mod Manager (UMM)](https://www.nexusmods.com/site/mods/21)
- [CommsRadioAPI](https://www.nexusmods.com/derailvalley/mods/740)

---

## Building from Source

**Requirements:** Visual Studio 2022 (or MSBuild), .NET Framework 4.7.2

1. Clone the repo and switch to this branch: `git checkout Debug`
2. Set `DVInstallPath` in `DVDRouteManager.csproj` to your Derail Valley install.
3. Build:
   ```
   MSBuild DVRouteManager\DVDRouteManager.csproj /p:Configuration=Debug
   ```
   The DLL is output to `bin\Debug\DVRouteManager.dll` and copied to your Mods folder automatically.

---

## Credits

- [WallyCZ/DVRouteManager](https://github.com/WallyCZ/DVRouteManager) — original mod
- [RouteSetter](https://github.com/zelmer69/RouteSetter) by zelmer69 — reference for modern CommsRadioAPI usage
- Derail Valley by [Altfuture](https://altfuture.gg/)
