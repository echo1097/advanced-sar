using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace AdvancedSar
{
    [HarmonyPatch(typeof(ObjectBaseViewModel), nameof(ObjectBaseViewModel.GetContextMenuItems))]
    internal static class SarOrders
    {
        private const double pickupSeconds = 4 * 60;
        private const double landingSeconds = 2 * 60;
        private const double reserveFraction = 0.2;
        private const double minimumReserveSeconds = 5 * 60;
        private const double secondsPerHour = 60 * 60;
        private const double kilometersPerNauticalMile = 1.852;
        private const double routeCheckIntervalSeconds = 30;
        private const double waypointUpdateDistanceNmi = 0.1;

        private static readonly List<(Helicopter helicopter, ObjectBase raft, GoToWaypointTask waypoint, GeoPosition destination)> assignments =
            new List<(Helicopter, ObjectBase, GoToWaypointTask, GeoPosition)>();
        private static double lastRouteCheck;

        private static void Postfix(ObjectBaseViewModel __instance, TrulyObservableCollection<ContextMenuItem> __result)
        {
            if (!(__instance._objectBase is Helicopter helicopter) || helicopter == null ||
                helicopter._taskforce == null || !helicopter.AcceptsOrdersFromPlayer)
                return;

            string ordersLabel = LanguageResourceHandler.Instance.getText("ContextMenu", "Orders", true, true);
            foreach (var menuItem in __result)
            {
                if (menuItem.Items == null || !string.Equals(menuItem.Label as string, ordersLabel))
                    continue;

                var sarItems = new TrulyObservableCollection<ContextMenuItem>
                {
                    new ContextMenuItem("Begin SAR", command: new DelegateCommand(_ => beginSar(helicopter))),
                    new ContextMenuItem("Cancel SAR", command: new DelegateCommand(_ => cancelSar(helicopter)))
                };
                menuItem.Items.Add(new ContextMenuItem("Search & Rescue", items: sarItems));
                return;
            }
        }

        private static void beginSar(Helicopter helicopter)
        {
            if (!canReceiveSarOrders(helicopter))
                return;

            refreshAssignments();
            if (assignments.Exists(assignment => assignment.helicopter == helicopter))
            {
                helicopter.ShowOrderFeedback("SAR already in progress", 5f);
                return;
            }

            var target = findReachableSarTarget(helicopter, out string failureMessage);
            if (target == null)
            {
                if (failureMessage != null)
                    showUnableToComply(helicopter, failureMessage);
                else
                    helicopter.ShowOrderFeedback("No unassigned friendly life rafts found", 5f);

                return;
            }

            var destination = new GeoPosition(target.getGeoPosition(), helicopter.DesiredAltitude.Value);
            if (helicopter.CurrentOrder.Value.OrderType != Order.Type.None)
                helicopter.setOrder(Order.Type.None, (ObjectBase)null, false);

            var formation = helicopter.Formation;
            if (formation?.LeaderStation != null && formation.LeaderStation.UnitObject != helicopter)
                formation.MakeUnitActIndependently(helicopter);

            helicopter._patrolZone?.DetachUnit(helicopter);
            helicopter.RemoveWaypoints();
            var waypoint = helicopter.setWaypointTask(destination, "SAR", WaypointData.WaypointHeightState.Number);
            helicopter.CommandPosition = destination;

            if (assignments.Count == 0)
                lastRouteCheck = GameTime.missionElapsedTime;

            assignments.Add((helicopter, target, waypoint, waypoint.WaypointPosition));
        }

        private static void cancelSar(Helicopter helicopter)
        {
            if (!canReceiveSarOrders(helicopter))
                return;

            refreshAssignments();
            int index = assignments.FindIndex(assignment => assignment.helicopter == helicopter);
            if (index < 0)
            {
                helicopter.ShowOrderFeedback("No SAR order in progress", 5f);
                return;
            }

            removeSarWaypoint(index);
            helicopter.ShowOrderFeedback("SAR canceled", 5f);
        }

        internal static void updateRoutes()
        {
            if (assignments.Count == 0)
                return;

            double missionTime = GameTime.missionElapsedTime;
            if (missionTime >= lastRouteCheck && missionTime - lastRouteCheck < routeCheckIntervalSeconds)
                return;

            lastRouteCheck = missionTime;
            refreshAssignments();
            if (assignments.Count == 0 || !ObjectsManager.InstanceExists(false))
                return;

            var activeRafts = ObjectsManager.Instance._listOfAllRafts;
            for (int index = assignments.Count - 1; index >= 0; index--)
            {
                var (helicopter, raft, _, destination) = assignments[index];
                if (getFriendlySurvivorCount(raft, helicopter._taskforce) > 0 && activeRafts.Contains(raft))
                {
                    var raftPosition = raft.getGeoPosition();
                    if (GeoPosition.HaversineDistanceNmi(destination, raftPosition) >= waypointUpdateDistanceNmi)
                        moveSarWaypoint(index, raft, raftPosition);

                    continue;
                }

                var replacement = findReachableSarTarget(helicopter, out _);
                if (replacement != null)
                {
                    moveSarWaypoint(index, replacement, replacement.getGeoPosition());
                    continue;
                }

                removeSarWaypoint(index);
                helicopter.ShowOrderFeedback("No reachable friendly life rafts", 5f);
            }
        }

        internal static void clearAssignments()
        {
            assignments.Clear();
        }

        private static void moveSarWaypoint(int index, ObjectBase raft, GeoPosition position)
        {
            var assignment = assignments[index];
            var waypoint = assignment.waypoint;
            waypoint.UpdatePosition(new GeoPosition(position, waypoint.WaypointPosition._height));
            assignments[index] = (assignment.helicopter, raft, waypoint, waypoint.WaypointPosition);
        }

        private static void removeSarWaypoint(int index)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            var waypoint = assignment.waypoint;
            if (helicopter.CommandPosition is GeoPosition position && position.Equals(waypoint.WaypointPosition))
                helicopter.CommandPosition = null;

            helicopter._userRoot.DeleteTask(waypoint);
            assignments.RemoveAt(index);
        }

        private static void refreshAssignments()
        {
            assignments.RemoveAll(assignment => !hasUnchangedWaypoint(assignment.helicopter, assignment.waypoint, assignment.destination));
        }

        private static bool canReceiveSarOrders(Helicopter helicopter)
        {
            return helicopter != null && !helicopter.IsDestroyed && !helicopter.IsDespawned &&
                helicopter._taskforce != null && helicopter.AcceptsOrdersFromPlayer;
        }

        private static bool hasUnchangedWaypoint(Helicopter helicopter, GoToWaypointTask waypoint, GeoPosition destination)
        {
            if (!canReceiveSarOrders(helicopter) || helicopter._userRoot == null)
                return false;

            foreach (var task in helicopter._userRoot.TaskViewModels)
            {
                if (!ReferenceEquals(task?.Task, waypoint))
                    continue;

                var position = waypoint.WaypointPosition;
                return position.Latitude == destination.Latitude && position.Longitude == destination.Longitude;
            }

            return false;
        }

        private static ObjectBase findReachableSarTarget(Helicopter helicopter, out string failureMessage)
        {
            var capacity = helicopter._obp._transportCapacity;
            var onBoard = helicopter._obp.CurrentTransportedOnBoard.Value;
            if (capacity <= 0 || onBoard >= capacity || !helicopter.IsControllable || helicopter._userRoot == null)
            {
                failureMessage = "Unable to comply";
                return null;
            }

            var homeBase = helicopter.getHomeBase();
            if (homeBase == null || homeBase.IsDestroyed || homeBase.IsDespawned)
            {
                failureMessage = "No home ship or airbase available";
                return null;
            }

            double topSpeed = helicopter._hp._maxSpeedSeaLevel;
            double secondsRemaining = getFuelBudgetSeconds(helicopter);
            if (!double.IsFinite(topSpeed) || topSpeed <= 0 ||
                (!helicopter._unlimitedFuel && !double.IsFinite(secondsRemaining)))
            {
                failureMessage = "Unable to estimate SAR flight time";
                return null;
            }

            var target = findSarTarget(helicopter, homeBase.getGeoPosition(), topSpeed, secondsRemaining,
                out bool hasRaftBeyondFuelRange);
            failureMessage = target == null && hasRaftBeyondFuelRange ? "Insufficient fuel for a SAR round trip" : null;
            return target;
        }

        private static ObjectBase findSarTarget(Helicopter helicopter, GeoPosition homePosition, double topSpeed,
            double secondsRemaining, out bool hasRaftBeyondFuelRange)
        {
            hasRaftBeyondFuelRange = false;
            if (!ObjectsManager.InstanceExists(false))
                return null;

            var origin = helicopter.getGeoPosition();
            var taskforce = helicopter._taskforce;
            ObjectBase bestRaft = null;
            int fewestSurvivors = int.MaxValue;
            double bestDistance = double.PositiveInfinity;

            foreach (var raft in ObjectsManager.Instance._listOfAllRafts)
            {
                int survivors = getFriendlySurvivorCount(raft, taskforce);
                if (survivors <= 0 || survivors > fewestSurvivors)
                    continue;

                if (assignments.Exists(assignment => assignment.raft == raft))
                    continue;

                var raftPosition = raft.getGeoPosition();
                double distance = GeoPosition.HaversineDistanceNmi(origin, raftPosition);
                double returnDistance = GeoPosition.HaversineDistanceNmi(raftPosition, homePosition);
                if (!double.IsFinite(distance) || !double.IsFinite(returnDistance))
                    continue;

                double requiredSeconds = getRequiredFlightSeconds(distance + returnDistance, topSpeed);
                if (secondsRemaining < requiredSeconds)
                {
                    hasRaftBeyondFuelRange = true;
                    continue;
                }

                if (survivors < fewestSurvivors || distance < bestDistance)
                {
                    bestRaft = raft;
                    fewestSurvivors = survivors;
                    bestDistance = distance;
                }
            }

            return bestRaft;
        }

        private static int getFriendlySurvivorCount(ObjectBase raft, Taskforce taskforce)
        {
            if (raft == null || raft.IsDestroyed || raft.IsDespawned || raft._taskforce == null ||
                taskforce.RelationshipTo(raft._taskforce) != RelationsState.Friendly)
                return 0;

            return raft._obp.CurrentTransportedOnBoard.Value;
        }

        private static double getFuelBudgetSeconds(Helicopter helicopter)
        {
            if (helicopter._unlimitedFuel)
                return double.PositiveInfinity;

            var parameters = helicopter._hp;
            if (!double.IsFinite(parameters._emptyMass) || parameters._emptyMass <= 0)
                return double.NaN;

            double loadoutFactor = 1 + (double)parameters._loadoutMass / parameters._emptyMass;
            double burnFactor = Math.Max(1, parameters._hoveringFuelConsumption);
            double fuelPerSecond = parameters._maxSpeedSeaLevel * kilometersPerNauticalMile / secondsPerHour *
                loadoutFactor * burnFactor;
            if (!double.IsFinite(fuelPerSecond) || fuelPerSecond <= 0)
                return double.NaN;

            return helicopter.RangeInKm.Value / fuelPerSecond;
        }

        private static double getRequiredFlightSeconds(double distance, double topSpeed)
        {
            double tripSeconds = distance / topSpeed * secondsPerHour + pickupSeconds + landingSeconds;
            double reserveSeconds = Math.Max(minimumReserveSeconds, tripSeconds * reserveFraction);
            return tripSeconds + reserveSeconds;
        }

        private static void showUnableToComply(Helicopter helicopter, string feedback)
        {
            helicopter.ShowOrderFeedback(feedback, 5f);
            VoiceMessage.Instance.PlayVoiceMessage("UnableToComply", helicopter);
        }
    }
}
