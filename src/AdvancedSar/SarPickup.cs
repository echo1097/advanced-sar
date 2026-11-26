using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using UniRx;

namespace AdvancedSar
{
    internal static partial class SarOrders
    {
        private const float pickupHeightFeet = 20;
        private const float maxPickupSpeedKnots = 13;
        private const float altitudeToleranceFeet = 5;
        private const float departureClimbFeet = 150;
        private const float departureWaypointNmi = 1f;
        private const double departureClearanceNmi = 0.6;
        private const float stagingDistanceNmi = 0.7f;
        private const double descentDistanceNmi = 1.2;
        private const double stagingToleranceNmi = 0.2;
        private const double pickupDistanceNmi = 0.15;
        private const double hoverEntryDistanceNmi = 0.12;
        private const double pickupDriftNmi = 0.01;
        private const double approachProgressNmi = 0.02;
        private const double pickupCheckIntervalSeconds = 3;
        private const float minimumRealCheckInterval = 0.1f;
        private const double stallSeconds = 30;

        private static readonly Dictionary<GoToWaypointTask, Assignment> sarWaypoints =
            new Dictionary<GoToWaypointTask, Assignment>();
        private static readonly List<ObjectBase> noPickupRafts = new List<ObjectBase>();
        private const double settleSeconds = 15;
        private const float maxPickupHeightFeet = 55;
        private static double lastPickupCheck;
        private static float lastRealPickupCheck;

        private enum SarStage
        {
            Transit,
            Departure,
            Descend,
            Pickup,
            Hover,
            Holding
        }

        private sealed class Assignment
        {
            internal readonly Helicopter helicopter;
            internal readonly RelationsState survivorRelation;
            internal GoToWaypointTask waypoint;
            internal HashSet<ObjectBase> skippedRafts;
            internal ObjectBase raft;
            internal GeoPosition destination;
            internal GeoPosition departureOrigin;
            internal float approachHeading;
            internal SarStage stage;
            internal float? savedAltitude;
            internal bool retried;
            internal bool directApproach;
            internal bool? originalPickupPermission;
            internal IDisposable pickupSubscription;
            private int lastOnBoard;
            internal double stageStarted;
            internal double lastPickupProgress;
            internal double bestApproachDistance;
            internal GeoPosition lastPosition;
            internal double attemptDeadline;
            internal double hoverStarted;
            internal float lastHoverHeight;
            internal double heightStableSince;
            internal bool risky;
            internal bool hasTurn;
            internal long turnOrder;
            internal int holdSlot;
            internal bool holdReached;

            internal Assignment(Helicopter helicopter, ObjectBase raft, GoToWaypointTask waypoint, float approachHeading,
                RelationsState survivorRelation)
            {
                this.helicopter = helicopter;
                this.raft = raft;
                this.waypoint = waypoint;
                this.approachHeading = approachHeading;
                this.survivorRelation = survivorRelation;
                destination = waypoint.WaypointPosition;
            }

            internal void watchPickups()
            {
                var passengers = helicopter._obp.CurrentTransportedOnBoard;
                lastOnBoard = passengers.Value;
                pickupSubscription = passengers.Subscribe(onPassengersChanged);
            }

            private void onPassengersChanged(int onBoard)
            {
                int rescued = onBoard - lastOnBoard;
                lastOnBoard = onBoard;
                if (rescued <= 0 || stage != SarStage.Hover || !helicopter.RaftPickUpAllowed.Value)
                    return;

                lastPickupProgress = GameTime.missionElapsedTime;
            }
        }

        [HarmonyPatch(typeof(ObjectBase), "CheckForRaftPickUp")]
        private static class SarPickupPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var raftList = AccessTools.Field(typeof(ObjectsManager), nameof(ObjectsManager._listOfAllRafts));
                var filter = AccessTools.Method(typeof(SarPickupPatch), nameof(getPickupRafts));
                foreach (var instruction in instructions)
                {
                    yield return instruction;
                    if (instruction.LoadsField(raftList))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, filter);
                    }
                }
            }

            private static List<ObjectBase> getPickupRafts(List<ObjectBase> activeRafts, ObjectBase pickupUnit)
            {
                if (!(pickupUnit is Helicopter helicopter))
                    return activeRafts;

                var assignment = assignments.Find(current => current.helicopter == helicopter);
                if (assignment == null)
                    return activeRafts;

                if (assignment.stage != SarStage.Hover || !hasActiveRaft(assignment, activeRafts))
                    return noPickupRafts;

                return new List<ObjectBase> { assignment.raft };
            }
        }

        [HarmonyPatch(typeof(GoToWaypointTask), "OnUpdate")]
        private static class SarWaypointPatch
        {
            private static bool Prefix(GoToWaypointTask __instance)
            {
                if (!sarWaypoints.TryGetValue(__instance, out var assignment))
                    return true;

                bool hovering = assignment.stage == SarStage.Hover ||
                    (assignment.stage == SarStage.Holding && assignment.holdReached);
                assignment.helicopter.CommandPosition = hovering
                    ? (GeoPosition?)null
                    : __instance.WaypointPosition;
                return false;
            }
        }

        private static bool isPickupCheckDue(double missionTime, float realTime)
        {
            if (missionTime >= lastPickupCheck && missionTime - lastPickupCheck < pickupCheckIntervalSeconds)
                return false;

            if (realTime >= lastRealPickupCheck && realTime - lastRealPickupCheck < minimumRealCheckInterval)
                return false;

            lastPickupCheck = missionTime;
            lastRealPickupCheck = realTime;
            return true;
        }

        private static GeoPosition getStagingPoint(GeoPosition raftPosition, float approachHeading)
        {
            return raftPosition.GeoOffsetByDistanceOnHeading(stagingDistanceNmi, approachHeading);
        }

        private static GeoPosition getDeparturePoint(GeoPosition origin, GeoPosition raftPosition)
        {
            if (GeoPosition.HaversineDistanceNmi(origin, raftPosition) >= departureWaypointNmi)
                return raftPosition;

            return origin.GeoOffsetByDistanceOnHeading(departureWaypointNmi, origin.HeadingToPosition(raftPosition));
        }

        private static void startApproach(Assignment assignment, double missionTime)
        {
            var helicopter = assignment.helicopter;
            var position = helicopter.getGeoPosition();
            var raft = assignment.raft.getGeoPosition();
            var target = assignment.directApproach ? raft : getStagingPoint(raft, assignment.approachHeading);
            double distance = GeoPosition.HaversineDistanceNmi(position, target);
            assignment.risky = SeaPower.Environment.Instance.SeaState >= 9;
            assignment.lastPosition = position;
            assignment.stageStarted = missionTime;
            assignment.lastPickupProgress = missionTime;
            assignment.bestApproachDistance = distance;
            assignment.attemptDeadline = missionTime + pickupSeconds +
                3 * secondsPerHour * distance / Math.Max(1, helicopter._hp._maxSpeedSeaLevel);
            assignment.stage = SarStage.Transit;
            suspendPickup(assignment);
            moveSarWaypoint(assignment, target, helicopter.DesiredAltitude.Value);
            updateApproach(assignment, position, raft, missionTime);
        }

        private static bool crossedRadius(GeoPosition previous, GeoPosition current, GeoPosition target, double radius)
        {
            double end = GeoPosition.HaversineDistanceNmi(current, target);
            if (end <= radius)
                return true;

            double start = GeoPosition.HaversineDistanceNmi(previous, target);
            if (start <= radius)
                return true;

            double travel = GeoPosition.HaversineDistanceNmi(previous, current);
            if (travel < start - radius)
                return false;

            double angle = (previous.HeadingToPosition(target) - previous.HeadingToPosition(current)) * Math.PI / 180;
            double along = start * Math.Cos(angle);
            return along >= 0 && along <= travel && Math.Abs(start * Math.Sin(angle)) <= radius;
        }

        private static float lowAltitude(Assignment assignment)
        {
            return assignment.risky ? assignment.helicopter._hp._presetAltitudes[0] :
                Utils.heightInFeetToUnity(pickupHeightFeet);
        }

        private static void updateApproach(Assignment assignment, GeoPosition position, GeoPosition raft, double missionTime)
        {
            var helicopter = assignment.helicopter;
            var previous = assignment.lastPosition;
            var staging = getStagingPoint(raft, assignment.approachHeading);
            bool nearRaft = (!assignment.retried || assignment.stage == SarStage.Pickup || assignment.stage == SarStage.Hover) &&
                crossedRadius(previous, position, raft, hoverEntryDistanceNmi);
            var target = assignment.directApproach ? raft : staging;
            if (assignment.stage == SarStage.Transit &&
                (nearRaft || crossedRadius(previous, position, target, descentDistanceNmi)))
            {
                if (!assignment.hasTurn && isRaftBusy(assignment))
                {
                    startHolding(assignment, position, raft, missionTime);
                    return;
                }

                assignment.hasTurn = true;
                assignment.savedAltitude ??= (float)position._height;
                helicopter.DesiredAltitude.Value = lowAltitude(assignment);
                assignment.stage = SarStage.Descend;
                assignment.stageStarted = missionTime;
                moveSarWaypoint(assignment, target, lowAltitude(assignment));
                assignment.lastPickupProgress = missionTime;
            }

            if (assignment.stage == SarStage.Descend &&
                (nearRaft || assignment.directApproach || crossedRadius(previous, position, staging, stagingToleranceNmi)))
            {
                assignment.stage = SarStage.Pickup;
                assignment.stageStarted = missionTime;
                assignment.bestApproachDistance = GeoPosition.HaversineDistanceNmi(position, raft);
                assignment.lastPickupProgress = missionTime;
                moveSarWaypoint(assignment, raft, lowAltitude(assignment));
            }

            if (assignment.stage == SarStage.Pickup && nearRaft)
            {
                assignment.stage = SarStage.Hover;
                assignment.hoverStarted = missionTime;
                assignment.heightStableSince = missionTime;
                assignment.lastHoverHeight = helicopter.AltitudeOverSurface;
                assignment.lastPickupProgress = missionTime;
                helicopter.CommandPosition = null;
                helicopter.DesiredAltitude.Value = lowAltitude(assignment);
            }

            assignment.lastPosition = position;
            if (assignment.stage == SarStage.Hover)
                return;

            if (assignment.stage == SarStage.Pickup)
                target = raft;

            double distance = GeoPosition.HaversineDistanceNmi(position, target);
            if (distance <= assignment.bestApproachDistance - approachProgressNmi)
            {
                assignment.bestApproachDistance = distance;
                assignment.lastPickupProgress = missionTime;
            }

            if (GeoPosition.HaversineDistanceNmi(assignment.destination, target) >= pickupDriftNmi)
                moveSarWaypoint(assignment, target, assignment.destination._height);
        }

        private static void suspendPickup(Assignment assignment)
        {
            var permission = assignment.helicopter.RaftPickUpAllowed;
            assignment.originalPickupPermission ??= permission.Value;
            permission.Value = false;
        }

        private static void updatePickup(int index, double missionTime, List<ObjectBase> activeRafts)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            if (missionTime < assignment.stageStarted)
            {
                if (assignment.stage != SarStage.Departure)
                {
                    startApproach(assignment, missionTime);
                    return;
                }
                assignment.stageStarted = missionTime;
            }

            if (assignment.stage == SarStage.Departure)
            {
                updateDeparture(index, missionTime, activeRafts);
                return;
            }

            if (!hasRoom(helicopter) || !hasActiveRaft(assignment, activeRafts) || isCoveredByOthers(assignment))
            {
                continueSar(index);
                return;
            }

            if (assignment.stage == SarStage.Holding)
            {
                updateHolding(assignment, missionTime);
                return;
            }

            var raft = assignment.raft.getGeoPosition();
            var position = helicopter.getGeoPosition();
            updateApproach(assignment, position, raft, missionTime);
            if (assignment.stage == SarStage.Hover)
            {
                double distance = GeoPosition.HaversineDistanceNmi(position, raft);
                bool slow = helicopter.getVelocityInKnots() <= maxPickupSpeedKnots;
                float height = helicopter.AltitudeOverSurface;
                if (Math.Abs(height - assignment.lastHoverHeight) > Utils.heightInFeetToUnity(2))
                {
                    if (height < assignment.lastHoverHeight)
                        assignment.lastPickupProgress = missionTime;
                    assignment.lastHoverHeight = height;
                    assignment.heightStableSince = missionTime;
                }

                if (assignment.risky)
                {
                    int seaState = Math.Clamp(SeaPower.Environment.Instance.SeaState, 0, 12);
                    float floor = lowAltitude(assignment) + Constants.WAVE_HEIGHTS[seaState];
                    if (slow && height <= floor + Utils.heightInFeetToUnity(altitudeToleranceFeet) &&
                        missionTime - assignment.heightStableSince >= settleSeconds)
                    {
                        endRiskyApproach(index);
                        return;
                    }
                }
                else
                {
                    bool allowPickup = slow && distance <= pickupDistanceNmi &&
                        height <= Utils.heightInFeetToUnity(maxPickupHeightFeet);
                    if (allowPickup && !helicopter.RaftPickUpAllowed.Value)
                        assignment.lastPickupProgress = missionTime;
                    helicopter.RaftPickUpAllowed.Value = allowPickup;

                    if (slow && distance > pickupDistanceNmi && missionTime - assignment.hoverStarted >= settleSeconds)
                    {
                        assignment.stage = SarStage.Pickup;
                        assignment.bestApproachDistance = distance;
                        moveSarWaypoint(assignment, raft, lowAltitude(assignment));
                    }
                }
            }

            bool timedOut = missionTime >= assignment.attemptDeadline ||
                (assignment.stage != SarStage.Transit && missionTime - assignment.stageStarted >= pickupSeconds);
            double progressLimit = assignment.stage == SarStage.Transit ? 2 * stallSeconds : stallSeconds;
            if (timedOut || missionTime - assignment.lastPickupProgress >= progressLimit)
                retryOrSkip(index, missionTime);
        }

        private static void endRiskyApproach(int index)
        {
            var helicopter = assignments[index].helicopter;
            var home = helicopter.getHomeBase();
            endSar(index, null);
            const string message = "Too risky, unable to conduct SAR";
            reportStatus(helicopter, message);
            helicopter.ShowOrderFeedback(message, 5f);

            if (isAvailableHome(home) && helicopter.CurrentOrder.Value?.OrderType != Order.Type.ReturnToBase)
                helicopter.setOrder(Order.Type.ReturnToBase, home, displayOrderText: false);
            reportReturningSurvivors(helicopter);
        }

        private static void retryOrSkip(int index, double missionTime)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            if (assignment.risky)
            {
                endRiskyApproach(index);
                return;
            }

            if (!assignment.retried &&
                findReachableSarTarget(helicopter, assignment.survivorRelation, out _, retryRaft: assignment.raft) != null)
            {
                assignment.retried = true;
                assignment.directApproach = false;
                var raftPosition = assignment.raft.getGeoPosition();
                assignment.approachHeading = raftPosition.HeadingToPosition(helicopter.getGeoPosition());
                startApproach(assignment, missionTime);
                return;
            }

            skipRaft(assignment);
            reportStatus(helicopter, "Unable to complete pickup. Skipping this raft.");
            helicopter.ShowOrderFeedback("SAR pickup stalled. Skipping this raft", 5f);
            continueSar(index);
        }

        private static void updateDeparture(int index, double missionTime, List<ObjectBase> activeRafts)
        {
            var assignment = assignments[index];
            var position = assignment.helicopter.getGeoPosition();
            if (GeoPosition.HaversineDistanceNmi(assignment.departureOrigin, position) >= departureClearanceNmi &&
                position._height >= assignment.destination._height - Utils.heightInFeetToUnity(altitudeToleranceFeet))
            {
                if (hasActiveRaft(assignment, activeRafts) &&
                    findReachableSarTarget(assignment.helicopter, assignment.survivorRelation, out _,
                        retryRaft: assignment.raft) != null)
                    routeToRaft(assignment, assignment.raft, missionTime, justClearedShip: true);
                else
                    continueSar(index, justClearedShip: true);

                return;
            }

            if (missionTime - assignment.stageStarted >= pickupSeconds)
                endSar(index, "SAR ended. Helicopter did not clear the departure route");
        }

        private static void restorePickupPermission(Assignment assignment)
        {
            if (!assignment.originalPickupPermission.HasValue)
                return;

            if (assignment.helicopter != null)
                assignment.helicopter.RaftPickUpAllowed.Value = assignment.originalPickupPermission.Value;

            assignment.originalPickupPermission = null;
        }

        private static void skipRaft(Assignment assignment)
        {
            assignment.skippedRafts ??= new HashSet<ObjectBase>();
            pruneSkippedRafts(assignment.skippedRafts);
            assignment.skippedRafts.Add(assignment.raft);
        }

        private static void pruneSkippedRafts(HashSet<ObjectBase> skippedRafts)
        {
            if (skippedRafts == null || skippedRafts.Count == 0)
                return;

            skippedRafts.RemoveWhere(raft => raft == null || raft.IsDestroyed || raft.IsDespawned);
        }
    }
}
