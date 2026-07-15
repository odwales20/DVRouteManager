using CommandTerminal;
using DV.Logic.Job;
using DV.Signs;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace DVRouteManager
{
    public class LocoAI : LocoCruiseControl
    {
        private const float TARGET_SPEED_DEFAULT = 20.0f;
        private const float COUPLER_APPROACH_SPEED = 5.0f;
        private const float SPEED_LIMIT_TARGET_MARGIN = 5.0f; // ~3 mph headroom under sign-derived limits
        private const float REVERSE_COUPLER_CLEARANCE = 12.0f;
        private const ReversingStrategy FREIGHT_HAUL_REVERSING_STRATEGY = ReversingStrategy.OnlyIfNeeded;
        private RouteTracker RouteTracker;

        public bool IsRunning => running;
        private bool _freightHaulActive = false;
        public bool IsFreightHaulActive => _freightHaulActive;
        private bool _reverseBlockedLogged = false;

        // Per-instance cache: per-300m-segment speed profiles, mirroring SignPlacer.GetTrackSigns.
        // null entry = track is excluded (ShouldIncludeTrack / noSignsTrackNameMarks) -> 120 km/h.
        // Non-null = ordered list of (distFromTrackStart, speedLimitKmh), including 120 km/h reset segments.
        private readonly Dictionary<string, List<(float dist, float speed)>> _speedProfileCache
            = new Dictionary<string, List<(float dist, float speed)>>();

#if DEBUG
        private RailTrack _lastLookaheadLimitTrack = null;
        private float _lastLookaheadMinLimit = -1f;
        private RailTrack _lastCurrentTrack = null;
        private float _lastCurrentLimit = -1f;
#endif

        /// <summary>
        /// Returns true when the next 1–2 tracks ahead in the route path include
        /// a turntable that hasn't finished rotating to its target angle yet.
        /// The AI will hold TargetSpeed = 0 until this returns false.
        /// </summary>
        private bool IsApproachingRotatingTurntable()
        {
            if (PathFinder._turntableTrackToTRT == null || PathFinder._turntableTrackToTRT.Count == 0)
                return false;

            RailTrack currentTrack = trainCar?.Bogies[0]?.track;
            if (currentTrack == null || RouteTracker?.Route?.Path == null)
                return false;

            var path = RouteTracker.Route.Path;
            int idx = path.IndexOf(currentTrack);
            if (idx < 0) return false;

            // Look 1–2 steps ahead in the path
            for (int i = idx + 1; i < Mathf.Min(idx + 3, path.Count); i++)
            {
                TurntableRailTrack trt;
                if (!PathFinder._turntableTrackToTRT.TryGetValue(path[i], out trt) || trt == null)
                    continue;
                if (Mathf.Abs(Mathf.DeltaAngle(trt.currentYRotation, trt.targetYRotation)) > 1f)
                    return true;
            }

            return false;
        }

        private static string GetProfileCacheKey(RailTrack track, bool forward)
        {
            return $"{track.GetInstanceID()}:{forward}";
        }

        private static bool IsYardTrack(RailTrack track)
        {
            return track?.name?.StartsWith("[Y]") == true;
        }

        // Returns the cached speed profile for a track and travel direction (null = excluded, 120 km/h everywhere).
        private List<(float dist, float speed)> GetTrackProfile(RailTrack track, bool forward)
        {
            if (track == null) return null;
            string key = GetProfileCacheKey(track, forward);
            List<(float, float)> profile;
            if (_speedProfileCache.TryGetValue(key, out profile)) return profile;
            profile = ComputeTrackProfile(track, forward);
            _speedProfileCache[key] = profile;
            return profile;
        }

        private const float YARD_SPEED_LIMIT = 50f;

        private struct CurrentTrackPosition
        {
            public RailTrack track;
            public float distance;
        }

        private CurrentTrackPosition? GetCurrentTrackPosition()
        {
            if (trainCar?.Bogies == null || !trainCar.Bogies.Any())
                return null;

            var bogie = trainCar.Bogies
                .FirstOrDefault(b => b != null && !b.HasDerailed && b.track != null);
            if (bogie == null)
                return null;

            RailTrack track = bogie.track;
            float trackLength = (float)track.LogicTrack().length;
            float span = Mathf.Clamp((float)bogie.traveller.Span, 0f, trackLength);

            var path = RouteTracker?.Route?.Path;
            int index = path?.IndexOf(track) ?? -1;
            bool forward = GetRouteDirection(track, index);

            return new CurrentTrackPosition
            {
                track = track,
                distance = forward ? span : trackLength - span
            };
        }

        private float GetSpeedAtTrackDistance(RailTrack track, bool forward, float distance)
        {
            // Yard tracks ([Y] prefix) are excluded from sign placement but should still be slow
            if (IsYardTrack(track)) return YARD_SPEED_LIMIT;
            var profile = GetTrackProfile(track, forward);
            if (profile == null || profile.Count == 0) return 120f;

            float speed = profile[0].speed;
            foreach (var (dist, spd) in profile)
            {
                if (dist > distance + 0.1f)
                    break;
                speed = spd;
            }
            return speed;
        }

        private bool GetRouteDirection(RailTrack track, int index)
        {
            var path = RouteTracker?.Route?.Path;
            if (track == null || path == null)
                return true;

            RailTrack next = index >= 0 && index + 1 < path.Count ? path[index + 1] : null;
            if (next != null)
            {
                if (track.IsTrackOutBranch(next)) return true;
                if (track.IsTrackInBranch(next)) return false;
            }

            RailTrack prev = index > 0 ? path[index - 1] : null;
            if (prev != null)
            {
                if (track.IsTrackInBranch(prev)) return true;
                if (track.IsTrackOutBranch(prev)) return false;
            }

            return true;
        }

        /// <summary>
        /// Returns the most restrictive speed limit within braking distance ahead on the route.
        /// Per-segment position-aware: only applies a segment's limit when the segment start
        /// is within the lookahead window, so a tight section 2000m into a long track doesn't
        /// slow the AI until it's actually approaching that section.
        /// Mirrors SignPlacer.GetTrackSigns segment pipeline (BezierArcApproximation + MinimizeSpeedDifference).
        /// </summary>
        private float GetLookaheadSpeedLimit(RailTrack currentTrack, float currentSpeedKmh, float currentDistance)
        {
            var path = RouteTracker?.Route?.Path;
            if (path == null) return GetSpeedAtTrackDistance(currentTrack, true, currentDistance);

            int startIdx = path.IndexOf(currentTrack);
            if (startIdx < 0) return GetSpeedAtTrackDistance(currentTrack, true, currentDistance);

            bool currentForward = GetRouteDirection(currentTrack, startIdx);
            float currentLimit = GetSpeedAtTrackDistance(currentTrack, currentForward, currentDistance);
#if DEBUG
            if (currentLimit < 120f && (currentTrack != _lastCurrentTrack || currentLimit != _lastCurrentLimit))
            {
                Terminal.Log($"[SpeedLimit] CurrentTrack name={currentTrack?.name} dir={(currentForward ? "F" : "R")} pos={currentDistance:0}m -> {currentLimit} km/h");
                _lastCurrentTrack = currentTrack;
                _lastCurrentLimit = currentLimit;
            }
            else if (currentLimit >= 120f)
            {
                _lastCurrentTrack = null;
                _lastCurrentLimit = -1f;
            }
#endif

            // Game places UpcomingSpeedDown signs ~speed*2 m before the limit change.
            // Use speed*3 for a small safety margin.
            float lookaheadM = Mathf.Max(currentSpeedKmh * 3f, 100f);

            float minLimit = currentLimit;
            float currentTrackLength = (float)currentTrack.LogicTrack().length;
            float distAhead = Mathf.Max(0f, currentTrackLength - currentDistance);
#if DEBUG
            RailTrack limitTrack = null;
            float limitSegDistTotal = 0f;
#endif

            if (!IsYardTrack(currentTrack))
            {
                var currentProfile = GetTrackProfile(currentTrack, currentForward);
                if (currentProfile != null)
                {
                    foreach (var (segDist, segSpeed) in currentProfile)
                    {
                        float distToSeg = segDist - currentDistance;
                        if (distToSeg <= 0f) continue;
                        if (distToSeg >= lookaheadM) break;
                        if (segSpeed < minLimit)
                        {
                            minLimit = segSpeed;
#if DEBUG
                            limitTrack = currentTrack;
                            limitSegDistTotal = distToSeg;
#endif
                        }
                    }
                }
            }

            for (int i = startIdx + 1; i < path.Count && distAhead < lookaheadM; i++)
            {
                var t = path[i];
                if (t == null) break;

                // Yard tracks cap at YARD_SPEED_LIMIT regardless of geometry
                if (IsYardTrack(t))
                {
                    if (YARD_SPEED_LIMIT < minLimit)
                    {
                        minLimit = YARD_SPEED_LIMIT;
#if DEBUG
                        limitTrack = t;
                        limitSegDistTotal = distAhead;
#endif
                    }
                }
                else
                {
                    bool forward = GetRouteDirection(t, i);
                    var profile = GetTrackProfile(t, forward);
                    if (profile != null)
                    {
                        foreach (var (segDist, segSpeed) in profile)
                        {
                            float distToSeg = distAhead + segDist;
                            if (distToSeg >= lookaheadM) break;
                            if (segSpeed < minLimit)
                            {
                                minLimit = segSpeed;
#if DEBUG
                                limitTrack = t;
                                limitSegDistTotal = distToSeg;
#endif
                            }
                        }
                    }
                }

                distAhead += (float)t.LogicTrack().length;
            }

#if DEBUG
            if (minLimit < currentLimit && limitTrack != null)
            {
                if (limitTrack != _lastLookaheadLimitTrack || minLimit != _lastLookaheadMinLimit)
                {
                    Terminal.Log($"[SpeedLimit] Lookahead capped {currentLimit}→{minLimit} km/h by {limitTrack.name} ({limitSegDistTotal:0}m ahead)");
                    _lastLookaheadLimitTrack = limitTrack;
                    _lastLookaheadMinLimit = minLimit;
                }
            }
            else
            {
                _lastLookaheadLimitTrack = null;
                _lastLookaheadMinLimit = -1f;
            }
#endif

            return ApplySpeedLimitMargin(minLimit);
        }

        private static float ApplySpeedLimitMargin(float speedLimit)
        {
            if (speedLimit <= COUPLER_APPROACH_SPEED)
                return speedLimit;

            return Mathf.Max(COUPLER_APPROACH_SPEED, speedLimit - SPEED_LIMIT_TARGET_MARGIN);
        }

        // Exact copy of SignPlacer.GetTrackSigns pipeline:
        //   BezierArcApproximation(error=1f) → ChunkifyNumbers(300m) → MinimizeSpeedDifference(30f, 300f)
        // Returns per-segment (distFromTrackStart, speedLimitKmh) list, or null if excluded.
        // Includes unrestricted 120 km/h segments so the AI can speed up after a restriction.
        private static List<(float dist, float speed)> ComputeTrackProfile(RailTrack track, bool forward)
        {
            if (track?.curve == null) return null;
            // SignPlacer.ShouldIncludeTrack: skip tracks < 100m, [Y] prefix, [#] prefix
            if (track.curve.length < 100f) return null;
            string name = track.name;
            if (name == null || name.StartsWith("[Y]") || name.StartsWith("[#]")) return null;

            var arcs = new List<BezierArcApproximation.Arc>();
            BezierArcApproximation.CalculateArcs(track.curve, 1f, arcs); // error=1f matches game

            // ChunkifyNumbers requires all lengths > 0; filter zero-length arcs
            var validArcs = arcs.Where(a => a.Length > 0f).ToList();
            if (validArcs.Count == 0) return null;

            // ChunkifyNumbers(minSum=300f): groups arcs into ≥300m segments
            List<List<float>> chunks;
            try { chunks = SignPlacerUtils.ChunkifyNumbers(validArcs.Select(a => a.Length).ToList(), 300f); }
            catch (Exception e) { Module.mod.Logger.Log($"[SpeedLimit] ChunkifyNumbers {name}: {e.Message}"); return null; }

            // Build per-segment (distFromStart, minRadius, segLen)
            var segs = new List<(float dist, float speed, float len)>();
            int arcIdx = 0;
            float distFromStart = 0f;

            foreach (var chunk in chunks)
            {
                float minR = float.PositiveInfinity;
                float segLen = 0f;
                float segStart = distFromStart;
                foreach (float _ in chunk)
                {
                    float r = validArcs[arcIdx].r;
                    if (r < minR) minR = r;
                    segLen += validArcs[arcIdx].Length;
                    arcIdx++;
                }
                distFromStart += segLen;
                float speed = (minR == float.PositiveInfinity) ? 120f : RadiusToSpeed(minR);
                segs.Add((segStart, speed, segLen));
            }

            // MinimizeSpeedDifference: raises short high-speed segments before a slow one
            // (same params as game: threshold=30f, segLenThreshold=300f)
            var speedLengths = segs.Select(s => (s.speed, s.len)).ToList();
            foreach (var (op, idx, value) in SignPlacerUtils.MinimizeSpeedDifference(speedLengths, 30f, 300f))
            {
                if (op == SignPlacerUtils.Operation.Update && idx < segs.Count)
                {
                    var s = segs[idx];
                    segs[idx] = (s.dist, value, s.len);
                }
                // Insert ops place warning signs ahead of drops — irrelevant for AI lookahead
            }

            // Junction end cap: game caps last segment to 60 km/h when track ends at a junction.
            // outJunction = junction at the bezier t=1 end (forward direction).
            // inJunction  = junction at the bezier t=0 end (reverse direction).
            if (forward && track.outJunction != null && segs.Count > 0)
            {
                var last = segs[segs.Count - 1];
                if (last.speed > 60f) segs[segs.Count - 1] = (last.dist, 60f, last.len);
            }
            else if (!forward && track.inJunction != null && segs.Count > 0)
            {
                var first = segs[0];
                if (first.speed > 60f) segs[0] = (first.dist, 60f, first.len);
            }

            var profile = segs
                .Select(s => (s.dist, s.speed))
                .OrderBy(s => s.Item1)
                .ToList();

            if (!forward && profile.Count > 0)
            {
                float trackLength = (float)track.LogicTrack().length;
                profile = segs
                    .Select(s => (Mathf.Max(0f, trackLength - (s.dist + s.len)), s.speed))
                    .OrderBy(s => s.Item1)
                    .ToList();
            }

            var compactProfile = new List<(float dist, float speed)>();
            foreach (var point in profile)
            {
                if (compactProfile.Count == 0 || Mathf.Abs(compactProfile[compactProfile.Count - 1].speed - point.speed) > 0.1f)
                    compactProfile.Add(point);
            }
            profile = compactProfile;

#if DEBUG
            if (profile.Count > 0)
                Terminal.Log($"[SpeedLimit] Profile {name} dir={(forward ? "F" : "R")}: " + string.Join(", ", profile.Select(p => $"{p.dist:0}m->{p.speed}km/h")));
#endif

            return profile.Count > 0 ? profile : null;
        }

        private static float RadiusToSpeed(float minRadius)
        {
            if (minRadius < 50f)   return 10f;
            if (minRadius < 70f)   return 20f;
            if (minRadius < 95f)   return 30f;
            if (minRadius < 130f)  return 40f;
            if (minRadius < 170f)  return 50f;
            if (minRadius < 230f)  return 60f;
            if (minRadius < 360f)  return 70f;
            if (minRadius < 700f)  return 80f;
            if (minRadius < 900f)  return 90f;
            if (minRadius < 1200f) return 100f;
            return 120f;
        }

        /// <summary>Called at module startup — no longer needed but kept for compatibility.</summary>
        public static void BuildSignSpeedLimitCache() { }

        public LocoAI(ILocomotiveRemoteControl remoteControl, TrainCar car) :
            base(remoteControl, car)
        {
        }

        // Disables DriverAssist and SteamCruiseControl via reflection so they don't fight us.
        // Both mods are optional — failures are silently swallowed.
        private static void DisableCompetingMods()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // DriverAssist: EntityManager.Instance.Loco.Components.CruiseControl = null
            try
            {
                var asm = assemblies.FirstOrDefault(a => a.GetName().Name == "DriverAssist");
                if (asm != null)
                {
                    Type entityManagerType = asm.GetType("EntityManager");
                    FieldInfo instanceField = entityManagerType?.GetField("Instance");
                    object entityManager = instanceField?.GetValue(null);
                    FieldInfo locoField = entityManager?.GetType().GetField("Loco");
                    object loco = locoField?.GetValue(entityManager);
                    if (loco != null)
                    {
                        FieldInfo componentsField = loco.GetType().GetField("Components");
                        object components = componentsField?.GetValue(loco);
                        if (components != null)
                        {
                            PropertyInfo cruiseControlProp = components.GetType().GetProperty("CruiseControl");
                            cruiseControlProp?.SetValue(components, null);
                            Terminal.Log("DriverAssist cruise control disabled");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Module.mod.Logger.Log("DisableCompetingMods DriverAssist: " + e.Message);
            }

            // SteamCruiseControl: Main._cruiseControlManager.IsEnabled = false
            try
            {
                var asm = assemblies.FirstOrDefault(a => a.GetName().Name == "SteamCruiseControl");
                if (asm != null)
                {
                    Type mainType = asm.GetType("SteamCruiseControl.Main");
                    FieldInfo managerField = mainType?.GetField("_cruiseControlManager", BindingFlags.Static | BindingFlags.NonPublic);
                    object manager = managerField?.GetValue(null);
                    if (manager != null)
                    {
                        PropertyInfo isEnabledProp = manager.GetType().GetProperty("IsEnabled");
                        isEnabledProp?.SetValue(manager, false);
                        Terminal.Log("SteamCruiseControl disabled");
                    }
                }
            }
            catch (Exception e)
            {
                Module.mod.Logger.Log("DisableCompetingMods SteamCruiseControl: " + e.Message);
            }
        }

        public bool StartAI(RouteTracker routeTracker)
        {
            if (routeTracker == null || routeTracker.Route == null)
                return false;

            if(RouteTracker != null)
            {
                RouteTracker.Dispose();
            }

            RouteTracker = routeTracker;

            if (RouteTracker.TrackState != RouteTracker.TrackingState.BeforeStart && RouteTracker.TrackState != RouteTracker.TrackingState.OnStart)
                return false;

            DisableCompetingMods();
            RouteTracker.Route.AdjustSwitches();

            TargetSpeed = TARGET_SPEED_DEFAULT;

            if (!running)
            {
                running = true;
                Module.StartCoroutine(AICoroutine());
            }

            return true;
        }

        private IEnumerator ReleaseAllBrakes()
        {
            //// ── Release handbrakes on wagons ─────────────────────────────
            //foreach (TrainCar car in loco.trainset.cars)
            //{
            //    if (!car.IsLoco && car.brakeSystem.hasHandbrake)
            //    {
            //        car.brakeSystem.SetHandbrakePosition(0f);
            //        Terminal.Log($"Released handbrake on {car.logicCar.ID}");
            //    }
            //}

            // ── Release loco brakes (train + independent) ────────────────
            // Use a loop to step them down smoothly
            for (int i = 0; i < 10; i++)
            {
                remoteControl.UpdateIndependentBrake(-1.0f);
                remoteControl.UpdateBrake(-1.0f);

                yield return new WaitForSeconds(0.3f);
            }
        }

        private IEnumerator AICoroutine()
        {
            const float TIME_WAIT = 0.3f;

            Terminal.Log("Autonomous driver start");
            yield return null;

            bool shouldreverse = false;

            remoteControl.UpdateReverser(ToggleDirection.UP);
            yield return null;
            remoteControl.UpdateReverser(ToggleDirection.UP);
            yield return null;

            float prevSpeed = 0.0f;
            float targetAcceleration = 2.5f;
            float prevTime = Time.time;

            float timeDelta = TIME_WAIT;

            bool couplerApproach = false;

            RouteTracker.TrackingState lastState = RouteTracker.TrackState;

            yield return ReleaseAllBrakes();

            while (running)
            {
                float speed = Mathf.Abs(remoteControl.GetForwardSpeed() * 3.6f);
                float acceleration = (speed - prevSpeed) / timeDelta;

                bool stateChanged = false;

                if(lastState != RouteTracker.TrackState)
                {
                    lastState = RouteTracker.TrackState;
                    stateChanged = true;
                }

                if (couplerApproach)
                {
                    if (IsCouplerInRange(1.00f))
                    {
                        Terminal.Log("Coupler in range");
                        break; //stop train
                    }
                    else if (IsCouplerInRange(7.0f))
                    {
                        TargetSpeed = 1.0f;
                    }
                    else
                    {
                        TargetSpeed = COUPLER_APPROACH_SPEED;
                    }
                }
                else if (RouteTracker.TrackState == RouteTracker.TrackingState.RightHeading)
                {
                    if (RouteTracker.DistanceToFinish < 50.0f && !RouteTracker.Route.LastTrack.LogicTrack().IsFree(RouteTracker.Trainset)) //finds all couplers not only on right rail
                    {
                        TargetSpeed = COUPLER_APPROACH_SPEED;
                    }
                    else if (IsApproachingRotatingTurntable())
                    {
                        TargetSpeed = 0f; // hold until turntable finishes rotating
                    }
                    else
                    {
                        var currentPosition = GetCurrentTrackPosition();
                        TargetSpeed = currentPosition.HasValue ? GetLookaheadSpeedLimit(currentPosition.Value.track, speed, currentPosition.Value.distance) : 0f;
                    }
                }
                else if (RouteTracker.TrackState == RouteTracker.TrackingState.OnStart)
                {
                    var currentPosition = GetCurrentTrackPosition();
                    TargetSpeed = currentPosition.HasValue ? GetLookaheadSpeedLimit(currentPosition.Value.track, speed, currentPosition.Value.distance) : 0f;
                }
                else if (RouteTracker.TrackState == RouteTracker.TrackingState.StopTrainAfterSwitch)
                {
                    TargetSpeed = 10.0f;
                    if (!shouldreverse)
                    {
                        shouldreverse = true;
                    }
                }
                else if (RouteTracker.TrackState == RouteTracker.TrackingState.WrongHeading
                    || RouteTracker.TrackState == RouteTracker.TrackingState.ReverseTrain)
                {
                    if (stateChanged)
                    {
                        //https://www.wolframalpha.com/input/?i=InterpolatingPolynomial%5B%7B%7B5%2C+4%7D%2C+%7B100%2C+15%7D%7D%2C+x%5D
                        int brakeLevel = 11 * ((int)speed - 5) / 95 + 4;
                        float brakeTime = speed / 5.0f;
                        Module.StartCoroutine(BrakePulse(brakeLevel, brakeTime));
                        TargetSpeed = 0.0f;
                        shouldreverse = true;
                    }

                    if (speed < 3.0f && shouldreverse)
                    {
                        if (IsReversePathBlocked())
                        {
                            shouldreverse = false;
                            yield return ReleaseAllBrakes();
                            var currentPosition = GetCurrentTrackPosition();
                            TargetSpeed = currentPosition.HasValue ? GetLookaheadSpeedLimit(currentPosition.Value.track, speed, currentPosition.Value.distance) : TARGET_SPEED_DEFAULT;
                        }
                        else
                        {
                            yield return Module.StartCoroutine(Reverse());
                            shouldreverse = false;
                            _reverseBlockedLogged = false;
                            var currentPosition = GetCurrentTrackPosition();
                            TargetSpeed = currentPosition.HasValue ? GetLookaheadSpeedLimit(currentPosition.Value.track, speed, currentPosition.Value.distance) : 0f;
                        }
                    }
                }

                targetAcceleration = MaintainSpeed(targetAcceleration, timeDelta, speed, acceleration);

                prevSpeed = speed;

                yield return new WaitForSeconds(TIME_WAIT);

                timeDelta = Time.time - prevTime;
                prevTime = Time.time;

                if (RouteTracker.TrackState == RouteTracker.TrackingState.OutOfWay)
                    break;

                if (RouteTracker.TrackState == RouteTracker.TrackingState.OnFinish)
                {
                    if (RouteTracker.Route.LastTrack.LogicTrack().IsFree(RouteTracker.Trainset))
                    {
                        break;
                    }
                    else
                    {
                        //on last track is some other car so try to go close to it's coupler
                        couplerApproach = true;
                        Terminal.Log($"coupler approach");
                    }
                }
            }

            running = false;

            for (int i = 0; i < 10; i++)
            {
                remoteControl.UpdateIndependentBrake(10.0f);
                remoteControl.UpdateBrake(1.0f);
                remoteControl.UpdateThrottle(-10.0f);
                yield return new WaitForSeconds(0.3f);
            }

            if (RouteTracker != Module.ActiveRoute?.RouteTracker)
                RouteTracker.Dispose();
        }

        bool IsCouplerInRange(float range)
        {
            Coupler lastCoupler = CouplerLogic.GetLastCoupler(this.RouteTracker.Trainset.firstCar.frontCoupler);
            Coupler lastCoupler2 = CouplerLogic.GetLastCoupler(this.RouteTracker.Trainset.lastCar.rearCoupler);
            Coupler firstCouplerInRange = lastCoupler?.GetFirstCouplerInRange(range);
            Coupler firstCouplerInRange2 = lastCoupler2?.GetFirstCouplerInRange(range);
            return firstCouplerInRange != null || firstCouplerInRange2 != null;
        }

        bool IsReversePathBlocked()
        {
            Coupler leadingCoupler = GetCouplerLeadingAfterReverse();
            Coupler obstacle = leadingCoupler?.GetFirstCouplerInRange(REVERSE_COUPLER_CLEARANCE);
            if (obstacle == null)
                return false;

            if (!_reverseBlockedLogged)
            {
                Terminal.Log($"Reverse blocked: coupler occupied within {REVERSE_COUPLER_CLEARANCE:0}m");
                _reverseBlockedLogged = true;
            }
            return true;
        }

        Coupler GetCouplerLeadingAfterReverse()
        {
            bool currentlyForward = remoteControl.GetReverserSymbol().ToUpper() == "F";
            Coupler couplerOnNewLeadingSide = currentlyForward ? trainCar?.rearCoupler : trainCar?.frontCoupler;
            return CouplerLogic.GetLastCoupler(couplerOnNewLeadingSide);
        }

        IEnumerator Reverse()
        {
            bool direction = remoteControl.GetReverserSymbol().ToUpper() == "F";

            while (remoteControl.GetTargetThrottle() > Mathf.Epsilon || Mathf.Abs( remoteControl.GetForwardSpeed() ) > 0.1)
            {
                remoteControl.UpdateIndependentBrake(1.0f);
                remoteControl.UpdateThrottle(-100.0f);
                yield return null;
            }
            remoteControl.UpdateReverser(direction ? ToggleDirection.DOWN : ToggleDirection.UP);
            yield return null;
            remoteControl.UpdateReverser(direction ? ToggleDirection.DOWN : ToggleDirection.UP);
            yield return null;

            yield return ReleaseAllBrakes();
        }

        IEnumerator BrakePulse(int level, float waitTime)
        {
            for (int i = 0; i < level; i++)
            {
                remoteControl.UpdateBrake(1.0f);
                yield return new WaitForSeconds(0.1f);
            }

            yield return new WaitForSeconds(waitTime);

            for (int i = 0; i < level + 1; i++)
            {
                remoteControl.UpdateBrake(-1.0f);
                yield return new WaitForSeconds(0.1f);
            }
        }

        // ─── Freight haul ────────────────────────────────────────────────────

        /// <summary>Stops both the AI driving and any active freight haul.</summary>
        public void StopAll()
        {
            _freightHaulActive = false;
            Stop();
        }

        /// <summary>
        /// Starts a full freight haul: loco → cars → couple → release HB → destination → uncouple → apply HB.
        /// </summary>
        public void StartFreightHaul(RouteTask task, TrainCar loco)
        {
            _freightHaulActive = false; // abort any existing haul
            Stop();
            Module.StartCoroutine(FreightHaulCoroutine(task, loco));
        }

        private IEnumerator FreightHaulCoroutine(RouteTask task, TrainCar loco)
        {
            _freightHaulActive = true;

            // ── Phase 1: drive loco to freight cars ──────────────────────────
            Terminal.Log("Freight haul: phase 1 – routing to cars");

            Trainset freightTrainset = task.TrainSets.FirstOrDefault();
            if (freightTrainset == null)
            {
                Terminal.Log("Freight haul: no trainset in task");
                _freightHaulActive = false;
                yield break;
            }

            Track carTrack = freightTrainset.firstCar.Bogies[0].track.LogicTrack();
            Track locoTrack = Utils.GetRouteStartTrackForLoco(loco);
            if (locoTrack == null)
            {
                Terminal.Log("Freight haul: loco route start track not found");
                _freightHaulActive = false;
                yield break;
            }
            Terminal.Log($"Freight haul: phase 1 start from loco ({Utils.DescribeLocoTrainsetPosition(loco)})");

            bool alreadyCoupled = loco.trainset == freightTrainset;

            if (loco.trainset == freightTrainset)
            {
                Terminal.Log("Freight haul: already coupled to target trainset, skipping routing to cars");
            }
            else
            {

                var toCarsTask = Route.FindRoute(locoTrack, carTrack, FREIGHT_HAUL_REVERSING_STRATEGY, loco.trainset);
                while (!toCarsTask.IsCompleted) yield return null;

                if (!_freightHaulActive) yield break;

                if (toCarsTask.IsFaulted || toCarsTask.Result == null)
                {
                    Terminal.Log("Freight haul: cannot find route to cars – " + (toCarsTask.Exception?.InnerException?.Message ?? "null"));
                    _freightHaulActive = false;
                    yield break;
                }

                var chain1 = RouteTaskChain.FromDestination(carTrack, loco.trainset);
                var tracker1 = new RouteTracker(chain1, true);
                tracker1.SetRoute(toCarsTask.Result, loco.trainset);
                Module.ActiveRoute.Route = toCarsTask.Result;
                Module.ActiveRoute.RouteTracker = tracker1;

                if (!StartAI(tracker1))
                {
                    Terminal.Log("Freight haul: AI could not start route to cars");
                    _freightHaulActive = false;
                    yield break;
                }
                while (running && _freightHaulActive) yield return null;

                if (!_freightHaulActive) { Stop(); yield break; }

                // ── Phase 2: couple and release handbrakes ───────────────────────
                Terminal.Log("Freight haul: phase 2 – coupling");
                yield return TryCoupleAndReleaseHandbrakes(loco);
                yield return new WaitForSeconds(1.5f);

                if (!_freightHaulActive) yield break;

            }

            // ── Phase 3: drive to destination ────────────────────────────────
            Terminal.Log($"Freight haul: phase 3 – routing to {task.DestinationTrack.ID.FullID}");

            Track nowTrack = Utils.GetRouteStartTrackForLoco(loco);
            if (nowTrack == null)
            {
                Terminal.Log("Freight haul: loco route start track not found after coupling");
                _freightHaulActive = false;
                yield break;
            }
            Terminal.Log($"Freight haul: phase 3 start from loco ({Utils.DescribeLocoTrainsetPosition(loco)})");
            var toDestTask = Route.FindRoute(nowTrack, task.DestinationTrack, FREIGHT_HAUL_REVERSING_STRATEGY, loco.trainset);
            while (!toDestTask.IsCompleted) yield return null;

            if (!_freightHaulActive) yield break;

            if (toDestTask.IsFaulted || toDestTask.Result == null)
            {
                Terminal.Log("Freight haul: cannot find route to destination – " + (toDestTask.Exception?.InnerException?.Message ?? "null"));
                _freightHaulActive = false;
                yield break;
            }

            var chain2 = RouteTaskChain.FromDestination(task.DestinationTrack, loco.trainset);
            var tracker2 = new RouteTracker(chain2, true);
            tracker2.SetRoute(toDestTask.Result, loco.trainset);
            Module.ActiveRoute.Route = toDestTask.Result;
            Module.ActiveRoute.RouteTracker = tracker2;

            if (!StartAI(tracker2))
            {
                Terminal.Log("Freight haul: AI could not start route to destination");
                _freightHaulActive = false;
                yield break;
            }
            while (running && _freightHaulActive) yield return null;

            if (!_freightHaulActive) { Stop(); yield break; }

            // ── Phase 4: uncouple and apply handbrakes ───────────────────────
            Terminal.Log("Freight haul: phase 4 – uncoupling");
            yield return UncoupleAndApplyHandbrakes(loco);

            _freightHaulActive = false;
            Terminal.Log("Freight haul: complete!");
            Module.PlayClip(Module.trainEnd);
        }

        private IEnumerator TryCoupleAndReleaseHandbrakes(TrainCar loco)
        {
            // Couple any couplers at the ends of the current trainset that are in range
            Coupler frontEnd = CouplerLogic.GetLastCoupler(loco.trainset.firstCar.frontCoupler);
            Coupler rearEnd = CouplerLogic.GetLastCoupler(loco.trainset.lastCar.rearCoupler);
            frontEnd?.GetFirstCouplerInRange(2.5f)?.TryCouple();
            rearEnd?.GetFirstCouplerInRange(2.5f)?.TryCouple();
            yield return new WaitForSeconds(0.5f);

            // Release handbrakes on all non-loco cars now in the trainset
            foreach (TrainCar car in loco.trainset.cars)
            {
                if (!car.IsLoco && car.brakeSystem.hasHandbrake)
                {
                    car.brakeSystem.SetHandbrakePosition(0f);
                    Terminal.Log($"Released handbrake on {car.logicCar.ID}");
                }
            }
        }

        private IEnumerator UncoupleAndApplyHandbrakes(TrainCar loco)
        {
            // Record freight cars before uncoupling so we can apply their handbrakes after
            List<TrainCar> freightCars = loco.trainset.cars.Where(c => !c.IsLoco).ToList();

            if (loco.trainset.firstCar == loco)
                loco.rearCoupler.Uncouple();
            else if (loco.trainset.lastCar == loco)
                loco.frontCoupler.Uncouple();
            else
            {
                loco.frontCoupler.Uncouple();
                yield return null;
                loco.rearCoupler.Uncouple();
            }

            yield return new WaitForSeconds(0.5f);

            foreach (TrainCar car in freightCars)
            {
                if (car.brakeSystem.hasHandbrake)
                {
                    car.brakeSystem.SetHandbrakePosition(1f);
                    Terminal.Log($"Applied handbrake on {car.logicCar.ID}");
                }
            }
        }

    }
}
