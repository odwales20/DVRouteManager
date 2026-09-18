using CommandTerminal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace DVRouteManager.Compatibility
{
    internal struct SignalSpeedConstraint
    {
        public float TargetSpeedKmh;
        public float DistanceMeters;
        public string Aspect;
        public bool MustStop;
    }

    internal sealed class DVSignalsCompatibility
    {
        private const float LOOKAHEAD_METERS = 2000f;
        private const float STOP_BUFFER_METERS = 20f;
        private const float SERVICE_DECELERATION_MS2 = 0.38f;
        private const float RESTRICTED_SPEED_KMH = 40f;

        private static Type _signalType;
        private static Type _trackReserverType;
        private static bool _detectionAttempted;

        private readonly HashSet<object> _reservations = new HashSet<object>();
        private object _activeReservation;
        private float _nextScanTime;
        private List<object> _signals = new List<object>();

        public static bool IsAvailable
        {
            get
            {
                Detect();
                return _signalType != null;
            }
        }

        public SignalSpeedConstraint? GetConstraint(Route route, RailTrack currentTrack, float currentDistance, float lineTargetKmh)
        {
            if (!IsAvailable || route?.Path == null || currentTrack == null)
                return null;

            RefreshSignals();
            int currentIndex = route.Path.IndexOf(currentTrack);
            if (currentIndex < 0)
                return null;

            object nearest = null;
            float nearestDistance = float.PositiveInfinity;
            object nearestAspect = null;

            foreach (object signal in _signals)
            {
                if (ConvertToBool(Get(signal, "IsOff")))
                    continue;

                object controller = Get(signal, "Controller");
                object placement = GetNullableValue(Get(controller, "PlacementInfo"));
                RailTrack signalTrack = Get(placement, "Track") as RailTrack;
                if (signalTrack == null)
                    continue;

                int signalTrackIndex = route.Path.IndexOf(signalTrack);
                if (signalTrackIndex < currentIndex)
                    continue;

                bool routeForward = GetRouteDirection(route.Path, signalTrack, signalTrackIndex);
                int signalDirection = ConvertToInt(Get(placement, "Direction"), -1);
                if (signalDirection >= 0 && signalDirection != (routeForward ? 1 : 0))
                    continue;

                float span = ConvertToFloat(Get(placement, "Span"), 0f);
                float distance = DistanceAlongRoute(route.Path, currentIndex, currentDistance, signalTrackIndex, span, routeForward);
                if (distance < -2f || distance > LOOKAHEAD_METERS || distance >= nearestDistance)
                    continue;

                object aspect = Get(signal, "CurrentAspect");
                if (aspect == null)
                    continue;

                nearest = signal;
                nearestAspect = aspect;
                nearestDistance = Mathf.Max(0f, distance);
            }

            if (nearest == null)
            {
                if (_activeReservation != null)
                    ClearReservation(_activeReservation);
                return null;
            }

            Wake(nearest);
            Reserve(nearest);

            string aspectId = Convert.ToString(Get(nearestAspect, "Id")) ?? "signal";
            object definition = Invoke(nearestAspect, "GetDefinition");
            bool mustStop = ConvertToBool(Get(nearestAspect, "DisallowPassing")) || ConvertToBool(Get(definition, "DisallowPassing"));
            float passingSpeed = ResolvePassingSpeed(nearestAspect, definition, aspectId, lineTargetKmh);
            float target = CalculateApproachSpeed(mustStop ? 0f : passingSpeed, nearestDistance, mustStop ? STOP_BUFFER_METERS : 0f);

            return new SignalSpeedConstraint
            {
                TargetSpeedKmh = Mathf.Min(lineTargetKmh, target),
                DistanceMeters = nearestDistance,
                Aspect = aspectId,
                MustStop = mustStop
            };
        }

        public void ReleaseAll()
        {
            if (_trackReserverType == null)
            {
                _reservations.Clear();
                _activeReservation = null;
                return;
            }

            MethodInfo clear = _trackReserverType.GetMethod("ClearFromSignal", BindingFlags.Public | BindingFlags.Static);
            foreach (object signal in _reservations.ToArray())
            {
                try { clear?.Invoke(null, new[] { signal }); }
                catch (Exception e) { Module.mod?.Logger?.Log("DVSignals release: " + e.GetBaseException().Message); }
            }
            _reservations.Clear();
            _activeReservation = null;
        }

        private static void Detect()
        {
            if (_detectionAttempted)
                return;
            _detectionAttempted = true;

            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Signals.Game");
            _signalType = assembly?.GetType("Signals.Game.Signal");
            _trackReserverType = assembly?.GetType("Signals.Game.Railway.TrackReserver");
            Module.mod?.Logger?.Log(_signalType != null
                ? "DVSignals detected: AI aspect control and route reservations enabled"
                : "DVSignals not detected: signal integration inactive");
        }

        private void RefreshSignals()
        {
            if (Time.time < _nextScanTime && _signals.Count > 0)
                return;
            _nextScanTime = Time.time + 5f;

            try
            {
                _signals = Resources.FindObjectsOfTypeAll(_signalType).Cast<object>().Where(s => s != null).ToList();
            }
            catch (Exception e)
            {
                Module.mod?.Logger?.Log("DVSignals scan: " + e.GetBaseException().Message);
                _signals.Clear();
            }
        }

        private void Reserve(object signal)
        {
            if (_trackReserverType == null || signal == null || _reservations.Contains(signal))
                return;
            try
            {
                MethodInfo has = _trackReserverType.GetMethod("HasReservation", BindingFlags.Public | BindingFlags.Static);
                MethodInfo reserve = _trackReserverType.GetMethod("ReserveForSignal", BindingFlags.Public | BindingFlags.Static);

                if (_activeReservation != null && !ReferenceEquals(_activeReservation, signal))
                    ClearReservation(_activeReservation);

                bool alreadyReserved = has != null && ConvertToBool(has.Invoke(null, new[] { signal }));
                if (alreadyReserved)
                    return;

                reserve?.Invoke(null, new[] { signal });
                _reservations.Add(signal);
                _activeReservation = signal;
            }
            catch (Exception e)
            {
                Module.mod?.Logger?.Log("DVSignals reservation: " + e.GetBaseException().Message);
            }
        }

        private void ClearReservation(object signal)
        {
            try
            {
                _trackReserverType?.GetMethod("ClearFromSignal", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { signal });
            }
            catch (Exception e)
            {
                Module.mod?.Logger?.Log("DVSignals release: " + e.GetBaseException().Message);
            }
            _reservations.Remove(signal);
            if (ReferenceEquals(_activeReservation, signal))
                _activeReservation = null;
        }

        private static void Wake(object signal)
        {
            try
            {
                object controller = Get(signal, "Controller");
                MethodInfo request = controller?.GetType().GetMethod("RequestUpdate", BindingFlags.Public | BindingFlags.Instance);
                request?.Invoke(controller, new object[] { 2 });
                object block = Get(signal, "Block");
                block?.GetType().GetMethod("FlagAsDirty", BindingFlags.Public | BindingFlags.Instance)?.Invoke(block, null);
            }
            catch { }
        }

        private static float ResolvePassingSpeed(object aspect, object definition, string id, float lineTarget)
        {
            string upper = (id ?? string.Empty).ToUpperInvariant();
            if (upper.StartsWith("NEXT_") || upper.Contains("DISTANT") || upper.Contains("REPEATER") || upper.Contains("VR"))
                return upper.Contains("STOP") || upper.Contains("VR0") ? RESTRICTED_SPEED_KMH : lineTarget;

            if (ConvertToBool(Get(definition, "UsePassingSpeed")))
            {
                float value = ConvertToFloat(Get(definition, "PassingSpeed"), 0f);
                if (value > 0f)
                    return value <= 35f ? value * 3.6f : value;
            }

            if (upper.Contains("RESTRICT") || upper.Contains("HP2") || upper.Contains("YELLOW") ||
                upper.Contains("SLOW") || upper.Contains("CAUTION") || upper.Contains("DIVERGING"))
                return RESTRICTED_SPEED_KMH;
            return lineTarget;
        }

        private static float CalculateApproachSpeed(float endSpeedKmh, float distance, float buffer)
        {
            float usableDistance = Mathf.Max(0f, distance - buffer);
            if (usableDistance <= 0f)
                return endSpeedKmh;
            float endMs = endSpeedKmh / 3.6f;
            return Mathf.Sqrt(endMs * endMs + 2f * SERVICE_DECELERATION_MS2 * usableDistance) * 3.6f;
        }

        private static float DistanceAlongRoute(IList<RailTrack> path, int currentIndex, float currentDistance, int signalIndex, float signalSpan, bool signalForward)
        {
            if (signalIndex == currentIndex)
            {
                float signalPosition = signalForward ? signalSpan : TrackLength(path[signalIndex]) - signalSpan;
                return signalPosition - currentDistance;
            }

            float distance = Mathf.Max(0f, TrackLength(path[currentIndex]) - currentDistance);
            for (int i = currentIndex + 1; i < signalIndex; i++)
                distance += TrackLength(path[i]);
            distance += signalForward ? signalSpan : TrackLength(path[signalIndex]) - signalSpan;
            return distance;
        }

        private static bool GetRouteDirection(IList<RailTrack> path, RailTrack track, int index)
        {
            RailTrack next = index + 1 < path.Count ? path[index + 1] : null;
            if (next != null)
            {
                if (track.IsTrackOutBranch(next)) return true;
                if (track.IsTrackInBranch(next)) return false;
            }
            RailTrack previous = index > 0 ? path[index - 1] : null;
            if (previous != null)
            {
                if (track.IsTrackInBranch(previous)) return true;
                if (track.IsTrackOutBranch(previous)) return false;
            }
            return true;
        }

        private static float TrackLength(RailTrack track) => track?.LogicTrack() != null ? (float)track.LogicTrack().length : 0f;
        private static object GetNullableValue(object value) => value == null ? null : Get(value, "Value") ?? value;
        private static object Get(object instance, string name)
        {
            if (instance == null) return null;
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null) return property.GetValue(instance, null);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(instance);
        }
        private static object Invoke(object instance, string name) => instance?.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(instance, null);
        private static bool ConvertToBool(object value) { try { return value != null && Convert.ToBoolean(value); } catch { return false; } }
        private static int ConvertToInt(object value, int fallback) { try { return value == null ? fallback : Convert.ToInt32(value); } catch { return fallback; } }
        private static float ConvertToFloat(object value, float fallback) { try { return value == null ? fallback : Convert.ToSingle(value); } catch { return fallback; } }
    }
}
