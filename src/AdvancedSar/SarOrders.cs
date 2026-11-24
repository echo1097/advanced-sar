using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace AdvancedSar
{
    [HarmonyPatch(typeof(ObjectBaseViewModel), nameof(ObjectBaseViewModel.GetContextMenuItems))]
    internal static partial class SarOrders
    {
        private const double pickupSeconds = 4 * 60;
        private const double landingSeconds = 2 * 60;
        private const double reserveFraction = 0.2;
        private const double minimumReserveSeconds = 5 * 60;
        private const double secondsPerHour = 60 * 60;
        private const double kilometersPerNauticalMile = 1.852;
        private const double routeCheckIntervalSeconds = 30;
        private const double waypointUpdateDistanceNmi = 0.1;

        private static readonly List<Assignment> assignments = new List<Assignment>();
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

            var raftPosition = target.getGeoPosition();
            float approachHeading = raftPosition.HeadingToPosition(helicopter.getGeoPosition());
            var destination = new GeoPosition(getStagingPoint(raftPosition, approachHeading), helicopter.DesiredAltitude.Value);
            if (!hasNoOrder(helicopter))
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

            var assignment = new Assignment(helicopter, target, waypoint, approachHeading);
            assignments.Add(assignment);
            sarWaypoints.Add(waypoint, assignment);
            updateTransit(assignment, GameTime.missionElapsedTime);
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

            endSar(index, "SAR canceled");
        }

        internal static void updateRoutes(float realTime)
        {
            if (assignments.Count == 0)
                return;

            double missionTime = GameTime.missionElapsedTime;
            bool checkRoutes = missionTime < lastRouteCheck || missionTime - lastRouteCheck >= routeCheckIntervalSeconds;
            bool checkPickup = activePickups > 0 && isPickupCheckDue(missionTime, realTime);
            if (!checkRoutes && !checkPickup)
                return;

            if (checkRoutes)
                lastRouteCheck = missionTime;

            refreshAssignments();
            if (assignments.Count == 0 || !ObjectsManager.InstanceExists(false))
                return;

            var activeRafts = ObjectsManager.Instance._listOfAllRafts;
            for (int index = assignments.Count - 1; index >= 0; index--)
            {
                var assignment = assignments[index];
                if (assignment.stage != SarStage.Transit)
                {
                    if (checkPickup)
                        updatePickup(index, missionTime, activeRafts);
                    continue;
                }

                if (!checkRoutes)
                    continue;

                if (!hasRoom(assignment.helicopter))
                    endSar(index, "SAR complete. Survivor capacity reached");
                else if (hasActiveRaft(assignment, activeRafts))
                    updateTransit(assignment, missionTime);
                else
                    continueSar(index);
            }
        }

        internal static void clearAssignments()
        {
            for (int index = assignments.Count - 1; index >= 0; index--)
            {
                if (hasUnchangedWaypoint(assignments[index]))
                    endSar(index, null);
                else
                    releaseAssignment(index);
            }
        }

        private static void updateTransit(Assignment assignment, double missionTime)
        {
            var raftPosition = assignment.raft.getGeoPosition();
            var stagingPoint = getStagingPoint(raftPosition, assignment.approachHeading);
            var helicopterPosition = assignment.helicopter.getGeoPosition();
            if (isAtPickupAltitude(assignment.helicopter) &&
                GeoPosition.HaversineDistanceNmi(helicopterPosition, raftPosition) <= hoverEntryDistanceNmi)
            {
                assignment.savedAltitude = (float)helicopterPosition._height;
                beginFinalApproach(assignment, raftPosition, missionTime);
                return;
            }

            if (GeoPosition.HaversineDistanceNmi(helicopterPosition, stagingPoint) <= descentDistanceNmi)
            {
                assignment.savedAltitude = (float)helicopterPosition._height;
                beginDescent(assignment, stagingPoint, missionTime);
                return;
            }

            if (GeoPosition.HaversineDistanceNmi(assignment.destination, stagingPoint) >= waypointUpdateDistanceNmi)
                moveSarWaypoint(assignment, stagingPoint, assignment.waypoint.WaypointPosition._height);
        }

        private static void continueSar(int index)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            assignment.raft = null;
            var target = findReachableSarTarget(helicopter, out string failureMessage, assignment.skippedRafts);
            if (target == null)
            {
                endSar(index, failureMessage ?? "No reachable friendly life rafts");
                return;
            }

            assignment.raft = target;
            assignment.retried = false;
            restorePickupPermission(assignment);
            setStage(assignment, SarStage.Transit);
            var raftPosition = target.getGeoPosition();
            assignment.approachHeading = raftPosition.HeadingToPosition(helicopter.getGeoPosition());
            moveSarWaypoint(assignment, getStagingPoint(raftPosition, assignment.approachHeading), helicopter.DesiredAltitude.Value);
            updateTransit(assignment, GameTime.missionElapsedTime);
        }

        private static void moveSarWaypoint(Assignment assignment, GeoPosition position, double altitude)
        {
            assignment.waypoint.UpdatePosition(new GeoPosition(position, altitude));
            assignment.destination = assignment.waypoint.WaypointPosition;
        }

        private static void endSar(int index, string feedback)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            bool restoreAltitude = assignment.stage != SarStage.Transit;
            releaseAssignment(index);
            helicopter._userRoot.DeleteTask(assignment.waypoint);
            if (helicopter.CommandPosition is GeoPosition position && position.Equals(assignment.destination))
                helicopter.CommandPosition = null;

            if (restoreAltitude)
                helicopter.DesiredAltitude.Value = assignment.savedAltitude;

            if (feedback != null)
                helicopter.ShowOrderFeedback(feedback, 5f);
        }

        private static void releaseAssignment(int index)
        {
            var assignment = assignments[index];
            restorePickupPermission(assignment);
            setStage(assignment, SarStage.Transit);
            sarWaypoints.Remove(assignment.waypoint);
            assignments.RemoveAt(index);
        }

        private static void refreshAssignments()
        {
            for (int index = assignments.Count - 1; index >= 0; index--)
            {
                var assignment = assignments[index];
                if (hasUnchangedWaypoint(assignment))
                    continue;

                var helicopter = assignment.helicopter;
                if (assignment.stage != SarStage.Transit && canReceiveSarOrders(helicopter) &&
                    helicopter.GetWaypoint(1) == null && hasNoOrder(helicopter) &&
                    helicopter.DesiredAltitude.Value == (float)assignment.destination._height)
                    helicopter.DesiredAltitude.Value = assignment.savedAltitude;

                releaseAssignment(index);
            }
        }

        private static bool canReceiveSarOrders(Helicopter helicopter)
        {
            return helicopter != null && !helicopter.IsDestroyed && !helicopter.IsDespawned &&
                helicopter._taskforce != null && helicopter.AcceptsOrdersFromPlayer;
        }

        private static bool hasUnchangedWaypoint(Assignment assignment)
        {
            var helicopter = assignment.helicopter;
            if (!canReceiveSarOrders(helicopter) || helicopter._userRoot == null || !hasNoOrder(helicopter) ||
                !ReferenceEquals(helicopter.GetWaypoint(1), assignment.waypoint))
                return false;

            var position = assignment.waypoint.WaypointPosition;
            var destination = assignment.destination;
            return position.Latitude == destination.Latitude && position.Longitude == destination.Longitude &&
                (assignment.stage == SarStage.Transit || position._height == destination._height);
        }

        private static bool hasNoOrder(Helicopter helicopter)
        {
            var order = helicopter.CurrentOrder.Value;
            return order == null || order.OrderType == Order.Type.None;
        }

        private static bool hasRoom(Helicopter helicopter)
        {
            return helicopter._obp._transportCapacity > 0 &&
                helicopter._obp.CurrentTransportedOnBoard.Value < helicopter._obp._transportCapacity;
        }

        private static bool hasActiveRaft(Assignment assignment, List<ObjectBase> activeRafts)
        {
            return getFriendlySurvivorCount(assignment.raft, assignment.helicopter._taskforce) > 0 &&
                activeRafts.Contains(assignment.raft);
        }

        private static ObjectBase findReachableSarTarget(Helicopter helicopter, out string failureMessage,
            HashSet<ObjectBase> skippedRafts = null, ObjectBase retryRaft = null)
        {
            if (!hasRoom(helicopter) || !helicopter.IsControllable || helicopter._userRoot == null)
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

            var target = findSarTarget(helicopter, homeBase.getGeoPosition(), topSpeed, secondsRemaining, skippedRafts, retryRaft,
                out bool hasRaftBeyondFuelRange);
            failureMessage = target == null && hasRaftBeyondFuelRange ? "Insufficient fuel for a SAR round trip" : null;
            return target;
        }

        private static ObjectBase findSarTarget(Helicopter helicopter, GeoPosition homePosition, double topSpeed,
            double secondsRemaining, HashSet<ObjectBase> skippedRafts, ObjectBase retryRaft, out bool hasRaftBeyondFuelRange)
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
                if (retryRaft != null && raft != retryRaft)
                    continue;

                int survivors = getFriendlySurvivorCount(raft, taskforce);
                if (survivors <= 0 || survivors > fewestSurvivors)
                    continue;

                if (skippedRafts?.Contains(raft) == true ||
                    assignments.Exists(assignment => assignment.raft == raft && assignment.helicopter != helicopter))
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
