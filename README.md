# DVRouteManager — Debug Branch

> **This is the active development branch. Code here is unfinished and may be unstable.**
> For the stable release see the [`master` branch](https://github.com/odwales20/DVRouteManager/tree/master).

A [Derail Valley](https://store.steampowered.com/app/588030/Derail_Valley/) mod that adds route management, automatic junction switching, cruise control, and autonomous AI driving via the Comms Radio.

> **Forked from [WallyCZ/DVRouteManager](https://github.com/WallyCZ/DVRouteManager)** — original mod by Wally.

---

## What's Different on This Branch

The `Debug` branch contains the in-progress **AI speed limit overhaul**. It is currently the branch used for live testing the autonomous driver speed-limit behaviour.

Current debug build marker: **b052**.

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
- DE6 throttle smoothing: amp protection now has hysteresis and DE6 throttle changes are slew-limited to reduce accelerator pulsing under AI control
- DE6 brake smoothing: predictive braking now holds its overspeed state briefly and ramps brake apply/release to reduce brake pulsing under AI control
- Steam brake smoothing: steam overspeed control now uses a capped, ramped service-brake target instead of dumping air with high/low brake pulses
- Destination roll-in recovery: if the AI is near the destination, has a roll-in target, but is stopped against applied brakes, it releases brakes automatically instead of needing AI re-engage
- Refuel routing restored: the main menu again offers routing to the nearest diesel fuel point, or water/coal point for steam locomotives
- DM3-specific DriverAssist behaviour: DM3 consists use manual-lap style train braking, and low-torque hill-climb throttle support is included alongside the existing DM3 gear shifting
- Non-self-lapping brake smoothing: loaded DM3 and other coarse notched/non-self-lapping train brakes now use the same capped, ramped service-brake style as steam instead of snapping between release and a large train-brake application
- Brake heat protection: steam and non-self-lapping AI service braking now scales brake demand down as trainset brake temperature/overheat percentage rises
- SteamCruiseControl-style braking: steam and non-self-lapping AI speed correction now uses an adapted pulse/heat/cylinder-release controller from SteamCruiseControl instead of a simple fixed service brake target
- Hot-brake speed reduction: when trainset brakes are hot/overheating, the AI lowers its effective cruise target before overspeed correction so it runs with more braking headroom on descents
- Yard reverse safety: before changing direction, the AI checks the coupler on the side that would become the leading end; if another coupler is within 12 m it keeps the current direction and continues at normal AI target speed instead of reversing into the cars
- Load hardening: missing debug audio files no longer throw startup exceptions, and the update check now has a short timeout
- DriverAssist compatibility: DriverAssist job-window registration exceptions from unsupported PassengerJobs task structures are suppressed so loading can continue
- Freight haul route preference: freight haul now uses `OnlyIfNeeded` reversing so it takes a forward/no-reverse route first and only reverses when no forward route can be found
- Loco AI route driving: the AI menu now has **Drive active route**, so an already-created/flipped/inspected route can be driven without picking a new destination
- AI route flipping: the Loco AI menu now has **Flip active route**, which quiet-stops any current AI drive, flips the active route direction, and returns to the AI menu; Drive active route rebuilds a fresh AI tracker before starting
- AI no-route safety: AI start and the AI coroutine now refuse to move the reverser or throttle unless their tracker still matches the active route; stopping AI also zeros target speed and closes steam regulator
- Loco-end route starts: AI routes created from the player locomotive now start from the actual loco bogie track rather than `trainset.firstCar`, with debug logging for attached car count and whether the loco is first/last/middle in the trainset
- Steam AI direction fix: steam cruise control now follows the actual reverser position instead of assuming positive AI target speed always means forward, so AI reversals can drive steam locos the correct way
- Steam route-start cutoff fix: AI startup no longer forces the steam cutoff/reverser forward; it picks forward or reverse from the route's first track transition
- Steam cutoff direction snap: route-start direction now mirrors SteamCruiseControl's cutoff thresholds (`>=0.52` forward, `<=0.48` reverse) and snaps steam cutoff to the requested side before the AI starts driving
- Steam cutoff direction lock: while AI is driving steam, every cutoff write is clamped to the selected side so coasting/stopping cannot drift to neutral and then get treated as forward
- Steam signed target direction: steam AI now mirrors SteamCruiseControl's reverse model by treating reverse AI movement as a negative effective target speed, then deriving cutoff direction from that signed target
- SteamCruiseControl-informed steam drive: S060/S282 AI now uses a pressure-target cutoff model with recovery/coast hysteresis and a fallback steam-chest pressure reader when the sim-flow port is unavailable
- DM3 reverse braking: route-reversal brake pulses are capped to about 7/11 train brake with a shorter hold, matching the controlled DM3 braking style instead of applying the generic heavy pulse
- DM3 speed cap: AI target speed is capped to 65 km/h on DM3, matching SteamCruiseControl's default
- Destination approach braking: the AI now starts reducing target speed inside 350 m of the destination but keeps a 10 km/h roll-in target before the finish trigger, then waits for a near-stop before final cleanup
- Destination siding clearance: after the route hits finish, the AI keeps rolling at 10 km/h until the tracked rear end of the train, plus an 8 m buffer, has entered the destination track before it performs the final stop
- Destination light-engine roll-in: final stopping now also requires the leading end to roll into the destination track, so shunting light engine should not stop as soon as the rear wheels touch the siding

### Known Remaining Issues

- Speed limiting on `Road`-prefixed tracks is still being tuned — the game removes signs from these tracks via `noSignsTrackNameMarks` (not readable at runtime), so our geometry-based limits may be slightly conservative on some road sections
- The AI now follows sign-derived limits with a fixed 5 km/h margin and DriverAssist-style controller protections, but braking and overspeed behaviour still needs testing across heavier consists, gradients, and poor adhesion
- Freight haul AI is not production-ready yet; speed-limit tuning is still in progress and heavy trains may still derail
- Yard reverse safety was added in b021/b022 but has not been live-tested yet
- If loading still sometimes sticks at 93%, check `UnityModManager/Log.txt`; b023 removes Route Manager's known missing-audio startup exceptions and limits its update check wait
- b024 suppresses the DriverAssist `OnRegisterJob` exception seen in `Player.log` when it cannot parse a PassengerJobs nested task
- Comms Radio reload is supported for testing, but a full game restart is still the safest way to confirm a clean mod load after larger code changes

### Next Test TODO

- Reload into **b052** and confirm the Comms Radio build marker updates after UMM reload
- Test Route to refuel: with a diesel loco confirm it offers Diesel fuel; with steam confirm it offers Water and Coal; each should build a route to the nearest matching point
- Test Loco AI -> Drive active route: create a normal route first, start it from the AI menu, and confirm it drives the existing route rather than computing a new destination
- Test Loco AI -> Flip active route: with a route that would push cars, flip it, then Drive active route and confirm it starts reliably and pulls from the other end without the old AI brake loop fighting it
- Test no-route safety: clear the active route, then try AI actions on diesel and steam; confirm neither moves forward and steam regulator closes
- Test freight haul after coupling and check the terminal log line `Freight haul: phase 3 start from loco (...)`; it should show the attached car count and whether the loco is `first` or `last` before routing to destination
- Test S060/S282 AI driving in both directions: create a short route, start Drive active route, then force a reverse/flip case and confirm cutoff follows the reverser rather than fighting it
- Test steam route-start direction: create a route that starts behind the steam loco, start Drive active route, and confirm the cutoff goes reverse instead of forcing forward into the consist
- Check S060/S282 steam pressure behavior: while accelerating above 20 km/h, cutoff should settle around pressure target instead of staying wide open; near target it should coast with regulator closed
- Test S060/S282 overspeed braking: confirm it holds a small/moderate service application instead of cycling release/apply/emergency during speed correction
- Re-test light-engine end-to-end driving with the 5 km/h speed-limit margin and DriverAssist-style protections enabled; watch for flange squeal, overspeed, high acceleration, wheel slip, and braking before tighter curves
- Test DM3 specifically: confirm gear shifting still works, loaded consists use controlled train braking, and hill climbs do not stall from over-aggressive throttle limiting
- Test loaded DM3/non-self-lapping overspeed braking: confirm it uses a steady service application instead of dumping/releasing the train brake while correcting speed
- Test long downhill braking: confirm the adapted SteamCruiseControl-style brake controller waits for brake cylinder release and reduces force/lengthens release as heat rises
- Test hot-brake slowdown: after cooking the brakes on a long descent, confirm the AI runs a few km/h slower than the normal sign/margin target until brakes cool
- Test DM3 reversal braking: force a wrong-heading/reverse state and confirm the log shows `DM3 reverse brake pulse` and the train does not get an excessive full-brake slam
- Test DM3 speed cap: on a route with a higher speed limit, confirm the DM3 AI target stays capped at 65 km/h
- Test destination approach braking with light engine and freight: confirm it eases down before the final track centre but does not stop short of the destination trigger
- Test destination siding clearance with a long freight consist: confirm it rolls past the destination trigger at about 10 km/h until the physical rear end of the train is inside the siding before stopping
- Test light-engine shunting into a siding: confirm it rolls deeper into the destination track instead of stopping as soon as the rear wheels enter it
- Test destination anti-stall recovery: if it stops short near the destination with brakes applied, confirm it releases brakes and resumes without manually re-engaging AI
- Test DE6 AI driving: confirm throttle and brake no longer audibly/visibly pulse while accelerating, holding speed, or correcting mild overspeed, and that amp/overspeed limiting still backs off under heavy load
- Test yard reverse safety: put cars close behind the loco/trainset, trigger an AI reversal, and confirm it keeps the other direction at normal AI target speed instead of reversing through them
- Repeat the yard reverse test with no cars behind it and confirm normal reversing still works
- Test freight haul route choice: with one clear end and one blocked/stock-heavy end, confirm the AI chooses the clear forward route and only reverses if no no-reverse route exists
- After light-engine testing looks stable, test a freight consist on the same route and check whether the margin is enough for heavier braking lag

### Shutdown Handoff

- Current branch: `Debug`, pushed to `origin/Debug`
- Current debug build marker: `b052`
- Current deployed DLL was built from this branch and copied to the local Derail Valley mod folder by the Debug build
- Last known live test before reloading: light engine completed an end-to-end map run without the 5 km/h safety margin; it sounded close to the limit on curves but did not derail
- b022 yard reverse safety has not been tested yet
- Next test should start by reloading into `b052` so hot-brake speed reduction, SteamCruiseControl-style pulse/heat/cylinder-release braking, non-self-lapping service-brake smoothing, steam service-brake smoothing, light-engine destination roll-in, SteamCruiseControl-style signed steam reverse target plus cutoff lock/snap, steam route-start cutoff direction, AI no-route safety, AI-menu flip/drive tracker rebuild, refuel routing, the 5 km/h margin, comm radio reload cleanup, DriverAssist-style protection layer, DM3-specific protection, DE6 throttle/brake smoothing, yard reverse safety, load hardening, DriverAssist job-registration compatibility patch, freight haul no-reverse preference, Drive active route AI option, loco-end route starts, steam AI direction fix, SteamCruiseControl-informed steam drive, capped DM3 reverse braking, 65 km/h DM3 speed cap, destination roll-in braking, destination anti-stall recovery, and rear-end based destination siding clearance are active
- Recent important commits:
  - b052 - reduce AI target speed while trainset brakes are hot
  - b051 - adapt SteamCruiseControl brake pulse and heat logic
  - b050 - reduce AI service braking as brakes heat up
  - b049 - smooth non-self-lapping AI service braking
  - b048 - smooth DM3 AI service braking
  - b047 - smooth steam AI overspeed braking
  - b046 - require light-engine roll-in before destination stop
  - b045 - mirror SteamCruiseControl signed reverse target for steam AI
  - b044 - lock steam cutoff direction during AI drive
  - b043 - snap steam cutoff direction using SteamCruiseControl thresholds
  - b042 - choose AI initial direction from route
  - b041 - prevent AI movement without active route
  - b040 - fix AI flip active route restart
  - b039 - restore nearest refuel routing menu
  - b038 - recover destination roll-in stalls and track train rear end
  - b037 - keep AI rolling into destination before final stop
  - b036 - smooth DE6 AI brake limiting
  - b035 - smooth DE6 AI throttle protection
  - b034 - require full consist in destination siding before final stop
  - b033 - add destination approach braking
  - b032 - cap DM3 AI speed at 65 km/h
  - b031 - cap DM3 reverse brake pulse
  - b030 - improve steam AI pressure-target driving
  - b029 - make steam AI follow reverser direction
  - b028 - start AI routes from the actual locomotive end
  - b027 - add AI-menu flip active route option
  - b026 - add AI drive active route option
  - b025 - prefer no-reverse freight haul routes
  - b024 - suppress DriverAssist PassengerJobs registration exceptions
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
- **Drive active route** — start the AI on the route already shown in Route Manager
- **Flip active route from AI menu** — reverse the active route direction before driving, quiet-stop the old AI, and rebuild a fresh tracker for Drive active route
- **AI no-route safety** — the AI refuses to move controls unless its tracker still matches the active route; stop closes steam regulator
- **Loco-end route starts** — AI route creation from the player loco uses the actual locomotive track and logs the trainset car count/front-rear position
- **Freight haul automation** — 4-phase: route to cars → couple → drive → deliver
- **Steam AI support** — S060/S282 use direct regulator/cutoff/brake control with pressure-target cutoff, fallback pressure reads, coasting hysteresis, and reverser-aware direction
- **Steam route-start cutoff direction** — AI startup chooses forward/reverse from the route's first move instead of forcing cutoff forward
- **Speed limit lookahead** — geometry-based, position-aware per-segment limits with a 5 km/h target margin
- **DriverAssist-style safety layer** — reduces throttle for heat, amps, wheel slip, and high acceleration; applies predictive braking before overspeed grows
- **DE6 throttle smoothing** — AI throttle changes are rate-limited and amp protection uses hysteresis to avoid rapid accelerator pulsing
- **DE6 brake smoothing** — predictive braking uses hysteresis and rate-limited apply/release to avoid rapid brake pulsing
- **Destination anti-stall recovery** — near the destination, the AI releases stuck brakes automatically when it should be rolling but has stopped
- **Refuel routing** — route to nearest diesel fuel point, or water/coal point for steam locomotives
- **DM3 reverse brake pulse cap** — wrong-heading/reversal stops avoid the generic heavy brake pulse on DM3 consists
- **DM3 speed cap** — DM3 AI target speed is capped to 65 km/h
- **Destination approach braking** — target speed tapers down before the final destination without stopping short of the finish trigger
- **Destination siding clearance** — after finish, the AI keeps rolling until the tracked rear end of the train has entered the destination track
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
