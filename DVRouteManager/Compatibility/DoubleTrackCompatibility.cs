using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVRouteManager.Compatibility
{
    internal static class DoubleTrackCompatibility
    {
        private const double MAINLINE_PREFERENCE = 0.85;
        private const double WRONG_WAY_PENALTY = 120.0;
        private const double CROSSOVER_PENALTY = 25.0;
        private static readonly Dictionary<RailTrack, bool> PreferredForward = new Dictionary<RailTrack, bool>();
        private static bool _initialized;

        public static double AdjustRouteCost(RailTrack previous, RailTrack current, RailTrack candidate, double cost)
        {
            if (candidate == null)
                return cost;
            EnsureInitialized();

            if (IsDoubleTrack(candidate))
            {
                cost *= MAINLINE_PREFERENCE;
                bool routeForward = current == null || candidate.IsTrackInBranch(current);
                bool preferred;
                if (PreferredForward.TryGetValue(candidate, out preferred) && routeForward != preferred)
                    cost += WRONG_WAY_PENALTY;
            }

            if (IsCrossover(candidate))
                cost += CROSSOVER_PENALTY;

            return cost;
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;
            _initialized = true;

            RailTrack[] tracks = UnityEngine.Object.FindObjectsOfType<RailTrack>();
            var candidates = new List<RailTrack>();
            foreach (RailTrack track in tracks)
            {
                if (IsDoubleTrack(track) && track?.curve != null)
                    candidates.Add(track);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                RailTrack first = candidates[i];
                Vector3 firstMid = first.curve.GetPointAt(0.5f);
                Vector3 firstTangent = Horizontal(first.curve.GetTangentAt(0.5f));
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    RailTrack second = candidates[j];
                    Vector3 secondMid = second.curve.GetPointAt(0.5f);
                    float separation = Vector3.Distance(firstMid, secondMid);
                    if (separation < 2.5f || separation > 12f)
                        continue;

                    Vector3 secondTangent = Horizontal(second.curve.GetTangentAt(0.5f));
                    float alignment = Vector3.Dot(firstTangent, secondTangent);
                    if (Mathf.Abs(alignment) < 0.85f)
                        continue;

                    Vector3 right = Vector3.Cross(Vector3.up, firstTangent).normalized;
                    bool secondIsRight = Vector3.Dot(secondMid - firstMid, right) > 0f;
                    PreferredForward[first] = !secondIsRight;
                    PreferredForward[second] = secondIsRight ? alignment > 0f : alignment <= 0f;
                    break;
                }
            }

            if (candidates.Count > 0)
                Module.mod?.Logger?.Log($"DoubleTrack support active: {candidates.Count} track sections, {PreferredForward.Count} paired lanes");
        }

        private static bool IsDoubleTrack(RailTrack track)
        {
            string name = track?.name ?? string.Empty;
            return name.IndexOf("DoubleTrack", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCrossover(RailTrack track)
        {
            string name = track?.name ?? string.Empty;
            return name.IndexOf("CX", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Crossover", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Cross", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Vector3 Horizontal(Vector3 vector)
        {
            vector.y = 0f;
            return vector.sqrMagnitude > Mathf.Epsilon ? vector.normalized : Vector3.forward;
        }
    }
}
