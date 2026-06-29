using CommandTerminal;
using DV;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DVRouteManager.CommsRadio
{
    public class RefuelPage : CRMSelectorPage
    {
        public RefuelPage(ICRMPageManager manager) : base(manager) { }

        protected override List<MenuItem> CreateMenuItems()
        {
            var menus = new List<MenuItem>();

            TrainCar loco = PlayerManager.LastLoco;
            if (loco == null)
            {
                menus.Add(new MenuItem("No locomotive found", null));
                menus.Add(GetExitMenu());
                return menus;
            }

            string locoTypeId = loco.carLivery?.parentType?.id ?? "";
            bool isSteam = locoTypeId.Contains("S060") || locoTypeId.Contains("S282");

            if (isSteam)
            {
                menus.Add(new MenuItem("Water", "Route", () => RouteToRefuel(ResourceType.Water)));
                menus.Add(new MenuItem("Coal", "Route", () => RouteToRefuel(ResourceType.Coal)));
            }
            else
            {
                menus.Add(new MenuItem("Diesel fuel", "Route", () => RouteToRefuel(ResourceType.Fuel)));
            }

            menus.Add(GetExitMenu());
            return menus;
        }

        private void RouteToRefuel(ResourceType resourceType)
        {
            TrainCar loco = PlayerManager.LastLoco;
            if (loco == null)
            {
                RedirectToMessagePage("No locomotive", "MENU");
                return;
            }

            Vector3 locoPos = loco.transform.position;

            // Collect all pit stops that offer the requested resource
            var candidates = new List<(float score, RailTrack track)>();

            var pits = Object.FindObjectsOfType<PitStopStation>();
            foreach (var pit in pits)
            {
                if (pit == null) continue;
                var modules = pit.locoResourceModules;
                if (modules?.resourceModules == null) continue;
                bool hasResource = modules.resourceModules.Any(m => m != null && m.resourceType == resourceType);
                if (!hasResource) continue;

                Vector3 pitPos = pit.transform.position;
                float distLocoToPit = Vector3.Distance(locoPos, pitPos);

                // Find the nearest RailTrack to this pit stop
                RailTrack nearestTrack = RailTrackRegistryBase.RailTracks
                    .Where(rt => rt != null && rt.LogicTrack() != null)
                    .OrderBy(rt => Vector3.Distance(rt.transform.position, pitPos))
                    .FirstOrDefault();

                if (nearestTrack == null) continue;

                float score = distLocoToPit;

                // For water, prefer drive-through tracks (water column over main line)
                // so the loco doesn't have to reverse out of a dead-end spur
                if (resourceType == ResourceType.Water &&
                    !(nearestTrack.inIsConnected && nearestTrack.outIsConnected))
                {
                    score += 2000f;
                }

                candidates.Add((score, nearestTrack));
            }

            if (candidates.Count == 0)
            {
                RedirectToMessagePage($"No {resourceType} station found", "MENU");
                return;
            }

            RailTrack bestTrack = candidates.OrderBy(c => c.score).First().track;
            string trackId = bestTrack.LogicTrack().ID.FullID;
            Terminal.Log($"Routing to {resourceType} station at track {trackId}");

            CommandArg[] args = new CommandArg[]
            {
                new CommandArg() { String = "from" },
                new CommandArg() { String = "loco" },
                new CommandArg() { String = "to" },
                new CommandArg() { String = trackId }
            };

            NewRoutePage.BuildRoute(args, this);
        }
    }
}
