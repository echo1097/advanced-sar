using System;
using System.Collections.Generic;
using SeaPower;

namespace AdvancedSar
{
    internal static partial class SarOrders
    {
        private const double clearDistanceNmi = 0.3;
        private const double clearSeconds = 60;
        private const double holdExitNmi = 0.3;
        private const float departurePruneInterval = 1f;
        private static readonly float[] holdOffsets = { 60, -60, 120, -120, 180 };

        private static readonly Dictionary<ObjectBase, Departure> raftDepartures =
            new Dictionary<ObjectBase, Departure>();
        private static long turnSequence;
        private static float lastDeparturePrune;

        private sealed class Departure
        {
            internal Helicopter helicopter;
            internal double leftAt;
        }

        private static int freeSeats(Helicopter helicopter)
        {
            return Math.Max(0, helicopter._obp._transportCapacity - helicopter._obp.CurrentTransportedOnBoard.Value);
        }

        private static int turnGroup(Assignment assignment)
        {
            if (assignment.hasTurn)
                return 0;

            return assignment.stage == SarStage.Holding ? 1 : 2;
        }

        private static bool isAhead(Assignment other, Assignment mine)
        {
            int otherGroup = turnGroup(other);
            int myGroup = turnGroup(mine);
            return otherGroup < myGroup || (otherGroup == myGroup && other.turnOrder < mine.turnOrder);
        }

        private static int seatsAhead(ObjectBase raft, Assignment mine)
        {
            int seats = 0;
            foreach (var other in assignments)
            {
                if (other == mine || other.raft != raft)
                    continue;

                if (mine == null || mine.raft != raft || isAhead(other, mine))
                    seats += freeSeats(other.helicopter);
            }

            return seats;
        }

        private static bool isCoveredByOthers(Assignment assignment)
        {
            if (assignment.hasTurn)
                return false;

            int survivors = getEligibleSurvivorCount(assignment.raft, assignment.helicopter._taskforce,
                assignment.survivorRelation);
            return survivors - seatsAhead(assignment.raft, assignment) <= 0;
        }

        private static void joinRaftQueue(Assignment assignment)
        {
            assignment.hasTurn = false;
            assignment.holdReached = false;
            assignment.turnOrder = ++turnSequence;
        }

        private static void leaveRaft(Assignment assignment)
        {
            if (assignment.hasTurn && assignment.raft != null && !assignment.raft.IsDestroyed &&
                !assignment.raft.IsDespawned && assignment.helicopter != null && !assignment.helicopter.IsDestroyed &&
                !assignment.helicopter.IsDespawned && assignment.helicopter._isInFlight)
            {
                double now = GameTime.missionElapsedTime;
                pruneDepartures(now);
                raftDepartures[assignment.raft] = new Departure { helicopter = assignment.helicopter, leftAt = now };
            }

            assignment.hasTurn = false;
            assignment.holdReached = false;
        }

        private static void updateDepartures(float realTime)
        {
            if (raftDepartures.Count == 0)
                return;

            if (!ObjectsManager.InstanceExists(false))
            {
                raftDepartures.Clear();
                return;
            }

            if (realTime >= lastDeparturePrune && realTime - lastDeparturePrune < departurePruneInterval)
                return;

            lastDeparturePrune = realTime;
            pruneDepartures(GameTime.missionElapsedTime);
        }

        private static void pruneDepartures(double now)
        {
            var stale = new List<ObjectBase>();
            foreach (var item in raftDepartures)
            {
                var raft = item.Key;
                var helicopter = item.Value.helicopter;
                if (raft == null || raft.IsDestroyed || raft.IsDespawned ||
                    helicopter == null || helicopter.IsDestroyed || helicopter.IsDespawned || !helicopter._isInFlight ||
                    now < item.Value.leftAt || now - item.Value.leftAt >= clearSeconds)
                    stale.Add(raft);
            }

            foreach (var raft in stale)
                raftDepartures.Remove(raft);
        }

        private static bool isDepartureClear(ObjectBase raft)
        {
            if (!raftDepartures.TryGetValue(raft, out var departure))
                return true;

            var helicopter = departure.helicopter;
            double now = GameTime.missionElapsedTime;
            bool clear = helicopter == null || helicopter.IsDestroyed || helicopter.IsDespawned ||
                !helicopter._isInFlight || now < departure.leftAt || now - departure.leftAt >= clearSeconds ||
                GeoPosition.HaversineDistanceNmi(helicopter.getGeoPosition(), raft.getGeoPosition()) >= clearDistanceNmi;
            if (clear)
                raftDepartures.Remove(raft);

            return clear;
        }

        private static bool isRaftBusy(Assignment assignment)
        {
            foreach (var other in assignments)
            {
                if (other == assignment || other.raft != assignment.raft)
                    continue;

                if (other.hasTurn)
                    return true;

                if (other.stage == SarStage.Holding &&
                    (assignment.stage != SarStage.Holding || other.turnOrder < assignment.turnOrder))
                    return true;
            }

            return !isDepartureClear(assignment.raft);
        }

        private static int firstFreeHoldSlot(Assignment assignment)
        {
            int slot = 0;
            while (assignments.Exists(other => other != assignment && other.raft == assignment.raft &&
                other.stage == SarStage.Holding && other.holdSlot == slot))
                slot++;

            return slot;
        }

        private static GeoPosition getHoldPoint(GeoPosition raftPosition, float approachHeading, int holdSlot)
        {
            float heading = (approachHeading + holdOffsets[holdSlot % holdOffsets.Length] + 360) % 360;
            float distance = stagingDistanceNmi * (1 + holdSlot / holdOffsets.Length);
            return raftPosition.GeoOffsetByDistanceOnHeading(distance, heading);
        }

        private static void startHolding(Assignment assignment, GeoPosition position, GeoPosition raftPosition,
            double missionTime)
        {
            var helicopter = assignment.helicopter;
            assignment.holdSlot = firstFreeHoldSlot(assignment);
            assignment.stage = SarStage.Holding;
            assignment.turnOrder = ++turnSequence;
            assignment.holdReached = false;
            assignment.stageStarted = missionTime;
            assignment.lastPosition = position;
            moveSarWaypoint(assignment, getHoldPoint(raftPosition, assignment.approachHeading, assignment.holdSlot),
                assignment.destination._height);

            helicopter.ShowOrderFeedback("SAR holding until the raft is clear", 5f);
            reportStatus(helicopter, "Holding for pickup.");
        }

        private static void updateHolding(Assignment assignment, double missionTime)
        {
            var helicopter = assignment.helicopter;
            var position = helicopter.getGeoPosition();
            var raftPosition = assignment.raft.getGeoPosition();
            if (!isRaftBusy(assignment))
            {
                assignment.hasTurn = true;
                assignment.holdReached = false;
                assignment.approachHeading = raftPosition.HeadingToPosition(position);
                startApproach(assignment, missionTime);
                reportStatus(helicopter, "Moving in for pickup.");
                return;
            }

            var holdPoint = getHoldPoint(raftPosition, assignment.approachHeading, assignment.holdSlot);
            if (GeoPosition.HaversineDistanceNmi(assignment.destination, holdPoint) >= pickupDriftNmi)
                moveSarWaypoint(assignment, holdPoint, assignment.destination._height);

            if (crossedRadius(assignment.lastPosition, position, holdPoint, hoverEntryDistanceNmi))
                assignment.holdReached = true;
            else if (GeoPosition.HaversineDistanceNmi(position, holdPoint) > holdExitNmi)
                assignment.holdReached = false;

            if (assignment.holdReached)
                helicopter.CommandPosition = null;

            assignment.lastPosition = position;
        }
    }
}
