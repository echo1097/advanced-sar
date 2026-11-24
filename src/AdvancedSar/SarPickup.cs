using System.Collections.Generic;
using HarmonyLib;
using SeaPower;

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
        private const double hoverExitDistanceNmi = 0.14;
        private const double pickupDriftNmi = 0.01;
        private const double approachProgressNmi = 0.02;
        private const double pickupCheckIntervalSeconds = 3;
        private const float minimumRealCheckInterval = 0.1f;
        private const double stallSeconds = 30;

        private static readonly Dictionary<GoToWaypointTask, Assignment> sarWaypoints =
            new Dictionary<GoToWaypointTask, Assignment>();
        private static int activePickups;
        private static double lastPickupCheck;
        private static float lastRealPickupCheck;

        private enum SarStage
        {
            Transit,
            Departure,
            Descend,
            Pickup,
            Hover
        }

        private sealed class Assignment
        {
            internal readonly Helicopter helicopter;
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
            internal int lastOnBoard;
            internal double stageStarted;
            internal double lastPickupProgress;
            internal double bestApproachDistance;
            internal bool reachedPickupPosition;

            internal Assignment(Helicopter helicopter, ObjectBase raft, GoToWaypointTask waypoint, float approachHeading)
            {
                this.helicopter = helicopter;
                this.raft = raft;
                this.waypoint = waypoint;
                this.approachHeading = approachHeading;
                destination = waypoint.WaypointPosition;
            }
        }

        [HarmonyPatch(typeof(GoToWaypointTask), "OnUpdate")]
        private static class SarWaypointPatch
        {
            private static bool Prefix(GoToWaypointTask __instance)
            {
                if (!sarWaypoints.TryGetValue(__instance, out var assignment))
                    return true;

                assignment.helicopter.CommandPosition = assignment.stage == SarStage.Hover
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

        private static void setStage(Assignment assignment, SarStage stage)
        {
            if (assignment.stage == SarStage.Transit && stage != SarStage.Transit)
                activePickups++;
            else if (assignment.stage != SarStage.Transit && stage == SarStage.Transit)
                activePickups--;

            assignment.stage = stage;
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

        private static bool isAtPickupAltitude(Helicopter helicopter)
        {
            return helicopter.AltitudeOverSurface <= Utils.heightInFeetToUnity(pickupHeightFeet + altitudeToleranceFeet);
        }

        private static void beginDescent(Assignment assignment, GeoPosition stagingPoint, double missionTime)
        {
            setStage(assignment, SarStage.Descend);
            assignment.stageStarted = missionTime;
            suspendPickup(assignment);
            moveSarWaypoint(assignment, stagingPoint, Utils.heightInFeetToUnity(pickupHeightFeet));
        }

        private static void beginFinalApproach(Assignment assignment, GeoPosition raftPosition, double missionTime)
        {
            var helicopter = assignment.helicopter;
            setStage(assignment, SarStage.Pickup);
            assignment.stageStarted = missionTime;
            assignment.lastPickupProgress = missionTime;
            assignment.bestApproachDistance = GeoPosition.HaversineDistanceNmi(helicopter.getGeoPosition(), raftPosition);
            assignment.reachedPickupPosition = false;
            assignment.lastOnBoard = helicopter._obp.CurrentTransportedOnBoard.Value;
            suspendPickup(assignment);
            moveSarWaypoint(assignment, raftPosition, Utils.heightInFeetToUnity(pickupHeightFeet));

            if (assignment.bestApproachDistance <= hoverEntryDistanceNmi && isAtPickupAltitude(helicopter))
                enterHover(assignment);
        }

        private static void enterHover(Assignment assignment)
        {
            setStage(assignment, SarStage.Hover);
            assignment.helicopter.CommandPosition = null;
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
                assignment.stageStarted = missionTime;
                assignment.lastPickupProgress = missionTime;
            }

            if (assignment.stage == SarStage.Departure)
            {
                updateDeparture(index, missionTime, activeRafts);
                return;
            }

            if (!hasRoom(helicopter) || !hasActiveRaft(assignment, activeRafts))
            {
                continueSar(index);
                return;
            }

            var raftPosition = assignment.raft.getGeoPosition();
            if (assignment.stage == SarStage.Descend)
            {
                var stagingPoint = assignment.directApproach ? raftPosition :
                    getStagingPoint(raftPosition, assignment.approachHeading);
                if (isAtPickupAltitude(helicopter) &&
                    GeoPosition.HaversineDistanceNmi(helicopter.getGeoPosition(), stagingPoint) <= stagingToleranceNmi)
                    beginFinalApproach(assignment, raftPosition, missionTime);
                else if (missionTime - assignment.stageStarted >= pickupSeconds)
                    retryOrSkip(index, missionTime);
                else if (GeoPosition.HaversineDistanceNmi(assignment.destination, stagingPoint) >= pickupDriftNmi)
                    moveSarWaypoint(assignment, stagingPoint, assignment.destination._height);

                return;
            }

            double distance = GeoPosition.HaversineDistanceNmi(helicopter.getGeoPosition(), raftPosition);
            bool atPickupAltitude = isAtPickupAltitude(helicopter);
            bool atPickupSpeed = helicopter.getVelocityInKnots() <= maxPickupSpeedKnots;
            if (assignment.stage == SarStage.Pickup && atPickupAltitude && distance <= hoverEntryDistanceNmi)
            {
                enterHover(assignment);
            }
            else if (assignment.stage == SarStage.Hover && atPickupSpeed && distance >= hoverExitDistanceNmi)
            {
                setStage(assignment, SarStage.Pickup);
                moveSarWaypoint(assignment, raftPosition, assignment.destination._height);
            }

            bool inPickupPosition = distance <= pickupDistanceNmi && atPickupAltitude && atPickupSpeed;
            if (helicopter.RaftPickUpAllowed.Value != inPickupPosition)
                helicopter.RaftPickUpAllowed.Value = inPickupPosition;

            int onBoard = helicopter._obp.CurrentTransportedOnBoard.Value;
            if (onBoard > assignment.lastOnBoard || (inPickupPosition && !assignment.reachedPickupPosition))
                assignment.lastPickupProgress = missionTime;

            assignment.reachedPickupPosition |= inPickupPosition;
            if (!assignment.reachedPickupPosition && distance <= assignment.bestApproachDistance - approachProgressNmi)
            {
                assignment.bestApproachDistance = distance;
                assignment.lastPickupProgress = missionTime;
            }

            assignment.lastOnBoard = onBoard;
            bool pickupStalled = missionTime - assignment.lastPickupProgress >= stallSeconds;
            if (pickupStalled || missionTime - assignment.stageStarted >= pickupSeconds)
            {
                retryOrSkip(index, missionTime);
                return;
            }

            if (assignment.stage == SarStage.Pickup &&
                GeoPosition.HaversineDistanceNmi(assignment.destination, raftPosition) >= pickupDriftNmi)
                moveSarWaypoint(assignment, raftPosition, assignment.destination._height);
        }

        private static void retryOrSkip(int index, double missionTime)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            if (!assignment.retried && findReachableSarTarget(helicopter, out _, retryRaft: assignment.raft) != null)
            {
                assignment.retried = true;
                assignment.directApproach = false;
                var raftPosition = assignment.raft.getGeoPosition();
                assignment.approachHeading = raftPosition.HeadingToPosition(helicopter.getGeoPosition());
                beginDescent(assignment, getStagingPoint(raftPosition, assignment.approachHeading), missionTime);
                return;
            }

            skipRaft(assignment);
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
                    findReachableSarTarget(assignment.helicopter, out _, retryRaft: assignment.raft) != null)
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
            assignment.skippedRafts.Add(assignment.raft);
        }
    }
}
