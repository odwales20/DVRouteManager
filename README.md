# DVRouteManager — Debug Branch

> **This is the active development branch. Code here is unfinished and may be unstable.**
> For the stable release see the [`master` branch](https://github.com/odwales20/DVRouteManager/tree/master).

A [Derail Valley](https://store.steampowered.com/app/588030/Derail_Valley/) mod that adds route management, automatic junction switching, cruise control, and autonomous AI driving via the Comms Radio.

> **Forked from [WallyCZ/DVRouteManager](https://github.com/WallyCZ/DVRouteManager)** — original mod by Wally.

---

## What's Different on This Branch

The `Debug` branch contains the in-progress **AI speed limit overhaul**. It is currently the branch used for live testing the autonomous driver speed-limit behaviour.

Current debug build marker: **b023**.

### AI Speed Limit System (WIP)

The new system mirrors the game's own `SignPlacer.GetTrackSigns` pipeline closely rather than trying to use a hand-made approximation:

- `BezierArcApproximation` (error=1f) computes curve arcs for each track
- `SignPlacerUtils.ChunkifyNumbers(300f)` groups arcs into 300 m segments
- `SignPlacerUtils.MinimizeSpeedDifference(30f, 300f)` raises the speed of short segments that drop too steeply (same parameters the game uses)
- Direction-aware speed profiles for both forward and reverse travel
- Unrestricted `120 km/h` reset segments are kept in the profile, so the AI can speed back up after leaving a restricted section
- **Per-segment, position-aware lookahead**: a tight section 1500 m into a long track does not slow the AI until it is actually within braking distance of that section — fixes the old "sitting at 60 for an entire track" problem
- **Current-position speed lookup**: the active limit is based on the current bogie position on the track, rather than the lowest limit anywhere on the track
- **AI target margin**: the AI targets 5 km/h below the sign-derived limit (about 3 mph) to give the cruise controller braking headroom
- Junction end cap: last segment capped to 60 km/h when a track leads into a junction
- Yard speed cap: `[Y]` tracks are limited to 50 km/h (these are excluded from sign placement but still need a sensible limit)
- DE4 startup fix: throttle capped at 25 % below 5 km/h to prevent traction motor overload from standstill
- Debug reload cleanup: UMM reload removes stale CommsRadioAPI modes so the Comms Radio build marker updates after reload
- DriverAssist-style controller protections: throttle backs off for projected overheating, excessive amps, wheel slip, and high acceleration; braking now uses a 10-second projected overspeed check
- DM3-specific DriverAssist behaviour: DM3 consists use manual-lap style train braking, and low-torque hill-climb throttle support is included alongside the existing DM3 gear shifting
- Yard reverse safety: before changing direction, the AI checks the coupler on the side that would become the leading end; if another coupler is within 12 m it keeps the current direction and continues at normal AI target speed instead of reversing into the cars
- Load hardening: missing debug audio files no longer throw startup exceptions, and the update check now has a short timeout

### Known Remaining Issues

- Speed limiting on `Road`-prefixed tracks is still being tuned — the game removes signs from these tracks via `noSignsTrackNameMarks` (not readable at runtime), so our geometry-based limits may be slightly conservative on some road sections
- The AI now follows sign-derived limits with a fixed 5 km/h margin and DriverAssist-style controller protections, but braking and overspeed behaviour still needs testing across heavier consists, gradients, and poor adhesion
- Freight haul AI is not production-ready yet; speed-limit tuning is still in progress and heavy trains may still derail
- Yard reverse safety was added in b021/b022 but has not been live-tested yet
- If loading still sometimes sticks at 93%, check `UnityModManager/Log.txt`; b023 removes Route Manager's known missing-audio startup exceptions and limits its update check wait
- Comms Radio reload is supported for testing, but a full game restart is still the safest way to confirm a clean mod load after larger code changes

### Next Test TODO

- Reload into **b023** and confirm the Comms Radio build marker updates after UMM reload
- Re-test light-engine end-to-end driving with the 5 km/h speed-limit margin and DriverAssist-style protections enabled; watch for flange squeal, overspeed, high acceleration, wheel slip, and braking before tighter curves
- Test DM3 specifically: confirm gear shifting still works, loaded consists use controlled train braking, and hill climbs do not stall from over-aggressive throttle limiting
- Test yard reverse safety: put cars close behind the loco/trainset, trigger an AI reversal, and confirm it keeps the other direction at normal AI target speed instead of reversing through them
- Repeat the yard reverse test with no cars behind it and confirm normal reversing still works
- After light-engine testing looks stable, test a freight consist on the same route and check whether the margin is enough for heavier braking lag

### Shutdown Handoff

- Current branch: `Debug`, pushed to `origin/Debug`
- Current debug build marker: `b023`
- Current deployed DLL was built from this branch and copied to the local Derail Valley mod folder by the Debug build
- Last known live test before reloading: light engine completed an end-to-end map run without the 5 km/h safety margin; it sounded close to the limit on curves but did not derail
- b022 yard reverse safety has not been tested yet
- Next test should start by reloading into `b023` so the 5 km/h margin, comm radio reload cleanup, DriverAssist-style protection layer, DM3-specific protection, yard reverse safety, and load hardening are active
- Recent important commits:
  - b023 - harden startup audio/update checks
  - b022 - continue normally when reversal is blocked
  - b021 - block reversals into nearby cars
  - b020 - include DM3 DriverAssist-style protections
  - b019 - add DriverAssist-style cruise protections
  - `abef2ed` - add AI speed limit margin
  - `7e53bc9` - remove stale comm radio modes on reload
  - `9100816` - fix debug reload and speed limit recovery

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
- **DM3 automatic gear shifting** — up at >800 RPM, down at <600 RPM, 70 km/h cap, plus manual-lap braking for DM3 consists
- **Steam loco support (S060 / S282)** — pressure-aware cutoff, pulse braking
- **Overheat protection** — throttle backed off at Warning, forced down at Critical

### Autonomous AI Driver
- **Drive to destination** — set a destination via Comms Radio; AI drives there automatically
- **Freight haul automation** — 4-phase: route to cars → couple → drive → deliver
- **Speed limit lookahead** — geometry-based, position-aware per-segment limits with a 5 km/h target margin
- **DriverAssist-style safety layer** — reduces throttle for heat, amps, wheel slip, and high acceleration; applies predictive braking before overspeed grows
- **Turntable awareness** — stops and waits for turntable to finish rotating
- **Competing mod safety** — disables DriverAssist and SteamCruiseControl on start

### Comms Radio UI
- New Route / Active Route / Loco AI / Settings
- Debug build marker shown on the Route Manager screen so live test builds can be identified in-game

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
| Freight haul AI | 🚧 Experimental — speed-limit tuning may still derail heavy trains |
| Map markers | ✅ Working |
| **AI speed limits** | 🚧 Light-engine testing only — freight/heavy consist tuning ongoing |
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
