using System;
using System.Collections.Generic;
using BepInEx.Logging;
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

        private static readonly List<Assignment> assignments = new List<Assignment>();
        private const string abortReport = "Rodger, aborting SAR.";
        private static double lastRouteCheck;
        internal static ManualLogSource logger;

        private static void Postfix(ObjectBaseViewModel __instance, TrulyObservableCollection<ContextMenuItem> __result)
        {
            var unit = __instance._objectBase;
            if (unit == null || unit._taskforce == null || !unit.AcceptsOrdersFromPlayer ||
                (!(unit is Helicopter) && !(unit is Vessel && isAvailableHome(unit))))
                return;

            string ordersLabel = LanguageResourceHandler.Instance.getText("ContextMenu", "Orders", true, true);
            foreach (var menuItem in __result)
            {
                if (menuItem.Items == null || !string.Equals(menuItem.Label as string, ordersLabel))
                    continue;

                var sarItems = new TrulyObservableCollection<ContextMenuItem>
                {
                    new ContextMenuItem("Begin SAR",
                        command: new DelegateCommand(_ => beginUnitSar(unit, RelationsState.Friendly))),
                    new ContextMenuItem("Begin SAR for\nEnemy Survivors",
                        command: new DelegateCommand(_ => beginUnitSar(unit, RelationsState.Hostile)))
                };
                if (unit is Helicopter helicopter)
                    sarItems.Add(new ContextMenuItem("Cancel SAR", command: new DelegateCommand(_ => cancelSar(helicopter))));
                menuItem.Items.Add(new ContextMenuItem("Search & Rescue", items: sarItems));
                return;
            }
        }

        private static void beginUnitSar(ObjectBase unit, RelationsState survivorRelation)
        {
            if (unit is Helicopter helicopter)
                beginSar(helicopter, survivorRelation);
            else if (isAvailableHome(unit) && unit.AcceptsOrdersFromPlayer)
                beginShipSar(unit, survivorRelation);
        }

        private static void beginSar(Helicopter helicopter, RelationsState survivorRelation)
        {
            if (!canReceiveSarOrders(helicopter))
                return;

            refreshAssignments();
            if (assignments.Exists(assignment => assignment.helicopter == helicopter) ||
                recoveries.Exists(recovery => recovery.helicopter == helicopter))
            {
                helicopter.ShowOrderFeedback("SAR already in progress", 5f);
                return;
            }

            var target = findReachableSarTarget(helicopter, survivorRelation, out string failureMessage);
            if (target == null)
            {
                if (failureMessage != null)
                    showUnableToComply(helicopter, failureMessage);
                else
                    helicopter.ShowOrderFeedback($"No {survivorSide(survivorRelation)} life rafts need a helicopter", 5f);

                return;
            }

            startSarFlight(helicopter, target, survivorRelation, null);
            reportStatus(helicopter, survivorRelation == RelationsState.Hostile
                ? "Starting SAR for enemy survivors."
                : "Starting SAR.");
        }

        private static string survivorSide(RelationsState survivorRelation)
        {
            return survivorRelation == RelationsState.Hostile ? "enemy" : "friendly";
        }

        private static void reportStatus(ObjectBase unit, string message)
        {
            Globals._mainGameViewModel?.RaiseAlert(unit, "${Name}: " + message);
        }

        private static string survivorCount(int count)
        {
            return count == 1 ? "1 survivor" : $"{count} survivors";
        }

        private static void startSarFlight(Helicopter helicopter, ObjectBase target, RelationsState survivorRelation,
            HashSet<ObjectBase> skippedRafts, bool afterTakeoff = false)
        {
            var origin = helicopter.getGeoPosition();
            var raftPosition = target.getGeoPosition();
            float approachHeading = raftPosition.HeadingToPosition(origin);
            var routePoint = afterTakeoff ? getDeparturePoint(origin, raftPosition) :
                getStagingPoint(raftPosition, approachHeading);
            double altitude = afterTakeoff ? Math.Max(helicopter.DesiredAltitude.Value,
                origin._height + Utils.heightInFeetToUnity(departureClimbFeet)) : helicopter.DesiredAltitude.Value;
            var destination = new GeoPosition(routePoint, altitude);
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

            var assignment = new Assignment(helicopter, target, waypoint, approachHeading, survivorRelation);
            assignment.skippedRafts = skippedRafts;
            joinRaftQueue(assignment);
            assignments.Add(assignment);
            assignment.watchPickups();
            sarWaypoints.Add(waypoint, assignment);
            if (afterTakeoff)
            {
                assignment.departureOrigin = origin;
                assignment.stageStarted = GameTime.missionElapsedTime;
                assignment.stage = SarStage.Departure;
                helicopter.DesiredAltitude.Value = (float)altitude;
            }
            else
            {
                startApproach(assignment, GameTime.missionElapsedTime);
            }
        }

        private static void cancelSar(Helicopter helicopter)
        {
            if (!canReceiveSarOrders(helicopter))
                return;

            refreshAssignments();
            int index = assignments.FindIndex(assignment => assignment.helicopter == helicopter);
            if (index < 0)
            {
                int recoveryIndex = recoveries.FindIndex(recovery => recovery.helicopter == helicopter);
                if (recoveryIndex >= 0)
                {
                    removeRecovery(recoveryIndex, true);
                    helicopter.ShowOrderFeedback("SAR canceled", 5f);
                    reportStatus(helicopter, "Canceling SAR.");
                    return;
                }

                helicopter.ShowOrderFeedback("No SAR order in progress", 5f);
                return;
            }

            endSar(index, "SAR canceled");
            reportStatus(helicopter, "Canceling SAR.");
        }

        internal static void updateRoutes(float realTime)
        {
            if (!ObjectsManager.InstanceExists(false))
            {
                if (assignments.Count > 0 || sarWaypoints.Count > 0 || raftDepartures.Count > 0 ||
                    recoveries.Count > 0 || sarLaunches.Count > 0 || shipLaunches.Count > 0 ||
                    coolingHelicopters.Count > 0)
                    clearAssignments();
                return;
            }

            updateDepartures(realTime);
            if (assignments.Count == 0)
                return;

            double missionTime = GameTime.missionElapsedTime;
            bool checkRoutes = missionTime < lastRouteCheck || missionTime - lastRouteCheck >= routeCheckIntervalSeconds;
            bool checkPickup = isPickupCheckDue(missionTime, realTime);
            if (!checkRoutes && !checkPickup)
                return;

            if (checkRoutes)
                lastRouteCheck = missionTime;

            refreshAssignments();
            if (assignments.Count == 0)
                return;

            var activeRafts = ObjectsManager.Instance._listOfAllRafts;
            for (int index = assignments.Count - 1; index >= 0; index--)
            {
                var assignment = assignments[index];
                if (checkRoutes && needsToReturnForFuel(assignment.helicopter))
                {
                    returnHome(index, "SAR returning for fuel");
                    continue;
                }

                if (checkPickup)
                    updatePickup(index, missionTime, activeRafts);
            }
        }

        internal static void clearAssignments()
        {
            for (int index = assignments.Count - 1; index >= 0; index--)
                tryEndAssignment(index);

            for (int index = recoveries.Count - 1; index >= 0; index--)
                tryRemoveRecovery(index);

            foreach (var leftover in assignments)
                leftover.pickupSubscription?.Dispose();

            clearShipOrders();
            assignments.Clear();
            sarWaypoints.Clear();
            raftDepartures.Clear();
            recoveries.Clear();
            sarLaunches.Clear();
        }

        private static void tryEndAssignment(int index)
        {
            try
            {
                if (hasUnchangedWaypoint(assignments[index]))
                    endSar(index, null);
                else
                    releaseAssignment(index);
            }
            catch (Exception error)
            {
                logger?.LogWarning($"Advanced SAR could not cleanly end a SAR assignment: {error.Message}");
            }
        }

        private static void tryRemoveRecovery(int index)
        {
            try
            {
                removeRecovery(index, true);
            }
            catch (Exception error)
            {
                logger?.LogWarning($"Advanced SAR could not cleanly end a SAR recovery: {error.Message}");
            }
        }

        private static void continueSar(int index, bool justClearedShip = false)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            if (!hasRoom(helicopter))
            {
                returnHome(index, "SAR complete. Survivor capacity reached");
                return;
            }

            leaveRaft(assignment);
            assignment.raft = null;
            var target = findReachableSarTarget(helicopter, assignment.survivorRelation, out string failureMessage,
                assignment.skippedRafts);
            if (target == null)
            {
                returnHome(index, failureMessage ?? $"No reachable {survivorSide(assignment.survivorRelation)} life rafts");
                return;
            }

            routeToRaft(assignment, target, GameTime.missionElapsedTime, justClearedShip);
        }

        private static void routeToRaft(Assignment assignment, ObjectBase target, double missionTime,
            bool justClearedShip = false)
        {
            var helicopter = assignment.helicopter;
            assignment.raft = target;
            joinRaftQueue(assignment);
            assignment.retried = false;
            var raftPosition = target.getGeoPosition();
            var origin = helicopter.getGeoPosition();
            assignment.approachHeading = raftPosition.HeadingToPosition(origin);
            assignment.directApproach = justClearedShip &&
                GeoPosition.HaversineDistanceNmi(origin, raftPosition) <= stagingDistanceNmi + stagingToleranceNmi;
            assignment.directApproach |= assignment.savedAltitude.HasValue;
            startApproach(assignment, missionTime);
        }

        private static void moveSarWaypoint(Assignment assignment, GeoPosition position, double altitude)
        {
            assignment.waypoint.UpdatePosition(new GeoPosition(position, altitude));
            assignment.destination = assignment.waypoint.WaypointPosition;
        }

        private static void endSar(int index, string feedback, bool restoreAltitude = true)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            removeSarWaypoint(assignment);
            releaseAssignment(index);

            if (restoreAltitude && assignment.savedAltitude is float altitude)
                helicopter.DesiredAltitude.Value = altitude;

            if (feedback != null)
                helicopter.ShowOrderFeedback(feedback, 5f);
        }

        private static void removeSarWaypoint(Assignment assignment)
        {
            var waypoint = assignment.waypoint;
            if (waypoint == null)
                return;

            var helicopter = assignment.helicopter;
            sarWaypoints.Remove(waypoint);
            helicopter._userRoot.DeleteTask(waypoint);
            assignment.waypoint = null;
            if (helicopter.CommandPosition is GeoPosition position && position.Equals(assignment.destination))
                helicopter.CommandPosition = null;
        }

        private static void releaseAssignment(int index)
        {
            var assignment = assignments[index];
            leaveRaft(assignment);
            assignment.pickupSubscription?.Dispose();
            restorePickupPermission(assignment);
            if (assignment.waypoint != null)
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
                bool routeInterrupted = canReceiveSarOrders(helicopter) && helicopter._userRoot != null &&
                    (!hasNoOrder(helicopter) || helicopter.GetWaypoint(1) != null);
                if (assignment.savedAltitude is float altitude && canReceiveSarOrders(helicopter) &&
                    helicopter.GetWaypoint(1) == null && hasNoOrder(helicopter) &&
                    helicopter.DesiredAltitude.Value == (float)assignment.destination._height)
                    helicopter.DesiredAltitude.Value = altitude;

                releaseAssignment(index);
                if (routeInterrupted)
                {
                    reportStatus(helicopter, abortReport);
                    reportReturningSurvivors(helicopter);
                }
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
                (!assignment.savedAltitude.HasValue || position._height == destination._height);
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
            return getEligibleSurvivorCount(assignment.raft, assignment.helicopter._taskforce, assignment.survivorRelation) > 0 &&
                activeRafts.Contains(assignment.raft);
        }

        private static ObjectBase findReachableSarTarget(Helicopter helicopter, RelationsState survivorRelation,
            out string failureMessage, HashSet<ObjectBase> skippedRafts = null, ObjectBase retryRaft = null)
        {
            if (!hasRoom(helicopter) || !helicopter.IsControllable || helicopter._userRoot == null)
            {
                failureMessage = "Unable to comply";
                return null;
            }

            var homeBase = helicopter.getHomeBase();
            if (!isAvailableHome(homeBase))
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

            var target = findSarTarget(helicopter.getGeoPosition(), homeBase.getGeoPosition(), helicopter._taskforce,
                survivorRelation, helicopter, topSpeed, secondsRemaining, skippedRafts, retryRaft,
                out bool hasRaftBeyondFuelRange);
            failureMessage = target == null && hasRaftBeyondFuelRange ? "Insufficient fuel for a SAR round trip" : null;
            return target;
        }

        private static ObjectBase findSarTarget(GeoPosition origin, GeoPosition homePosition, Taskforce taskforce,
            RelationsState survivorRelation, Helicopter helicopter, double topSpeed,
            double secondsRemaining, HashSet<ObjectBase> skippedRafts, ObjectBase retryRaft, out bool hasRaftBeyondFuelRange,
            double extraDistanceNmi = 0)
        {
            hasRaftBeyondFuelRange = false;
            if (!ObjectsManager.InstanceExists(false))
                return null;

            ObjectBase bestRaft = null;
            bool bestShared = false;
            int fewestSurvivors = int.MaxValue;
            double bestDistance = double.PositiveInfinity;
            var mine = helicopter == null ? null : assignments.Find(assignment => assignment.helicopter == helicopter);

            foreach (var raft in ObjectsManager.Instance._listOfAllRafts)
            {
                if (retryRaft != null && raft != retryRaft)
                    continue;

                int survivors = getEligibleSurvivorCount(raft, taskforce, survivorRelation);
                if (survivors <= 0 || skippedRafts?.Contains(raft) == true)
                    continue;

                int seatsComing = seatsAhead(raft, mine);
                bool shared = seatsComing > 0;
                survivors -= seatsComing;
                if (survivors <= 0)
                    continue;

                var raftPosition = raft.getGeoPosition();
                double distance = GeoPosition.HaversineDistanceNmi(origin, raftPosition);
                double returnDistance = GeoPosition.HaversineDistanceNmi(raftPosition, homePosition);
                if (!double.IsFinite(distance) || !double.IsFinite(returnDistance))
                    continue;

                double requiredSeconds = getRequiredFlightSeconds(distance + returnDistance + extraDistanceNmi, topSpeed);
                if (secondsRemaining < requiredSeconds)
                {
                    hasRaftBeyondFuelRange = true;
                    continue;
                }

                bool better = bestRaft == null;
                if (!better && shared != bestShared)
                    better = !shared;
                else if (!better && survivors != fewestSurvivors)
                    better = survivors < fewestSurvivors;
                else if (!better)
                    better = distance < bestDistance;

                if (better)
                {
                    bestRaft = raft;
                    bestShared = shared;
                    fewestSurvivors = survivors;
                    bestDistance = distance;
                }
            }

            return bestRaft;
        }

        private static int getEligibleSurvivorCount(ObjectBase raft, Taskforce taskforce, RelationsState survivorRelation)
        {
            if (raft == null || raft.IsDestroyed || raft.IsDespawned || raft._taskforce == null ||
                taskforce.RelationshipTo(raft._taskforce) != survivorRelation)
                return 0;

            return raft._obp.CurrentTransportedOnBoard.Value;
        }

        private static double getFuelBudgetSeconds(Helicopter helicopter)
        {
            if (helicopter._unlimitedFuel)
                return double.PositiveInfinity;

            return helicopter.RangeInKm.Value / getFuelBurnPerSecond(helicopter);
        }

        private static double getFuelBurnPerSecond(Helicopter helicopter)
        {
            var parameters = helicopter._hp;
            if (!double.IsFinite(parameters._emptyMass) || parameters._emptyMass <= 0)
                return double.NaN;

            double loadoutFactor = 1 + (double)parameters._loadoutMass / parameters._emptyMass;
            double burnFactor = Math.Max(1, parameters._hoveringFuelConsumption);
            double fuelPerSecond = parameters._maxSpeedSeaLevel * kilometersPerNauticalMile / secondsPerHour *
                loadoutFactor * burnFactor;
            if (!double.IsFinite(fuelPerSecond) || fuelPerSecond <= 0)
                return double.NaN;

            return fuelPerSecond;
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
