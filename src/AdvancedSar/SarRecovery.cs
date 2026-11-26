using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace AdvancedSar
{
    internal static partial class SarOrders
    {
        private static readonly List<Recovery> recoveries = new List<Recovery>();
        private static readonly Dictionary<LaunchTaskParameters, Recovery> sarLaunches =
            new Dictionary<LaunchTaskParameters, Recovery>();
        private static readonly AccessTools.FieldRef<AircraftRecoveryInventory, List<AircraftRecoveryInventory.Entry>> recoveryEntries =
            AccessTools.FieldRefAccess<AircraftRecoveryInventory, List<AircraftRecoveryInventory.Entry>>("entries");
        private static Recovery launchingRecovery;
        private static float lastRecoveryCheck;

        private sealed class Recovery
        {
            internal Helicopter helicopter;
            internal ObjectBase home;
            internal FlightDeck deck;
            internal RelationsState survivorRelation;
            internal HashSet<ObjectBase> skippedRafts;
            internal string recoveryId;
            internal bool shipLaunch;
            internal bool awaitingLaunch => shipLaunch || recoveryId != null;
            internal string loadoutName;
            internal int callsignIndex;
            internal int survivors;
            internal double fuelBurn;
            internal double topSpeed;
            internal float remainingRange;
            internal float fullRange;
            internal bool unlimitedFuel;
            internal bool healthy;
            internal float waypointTolerance;
            internal bool emcon;
            internal ObjectBase.WeaponStatus weaponStatus;
            internal float cooldownDuration;
            internal double landedAt;
            internal bool cooldownSkipped;
            internal bool quickTurnaround;
            internal bool retryWithRefuel;
            internal PendingLaunchTask pending;
            internal LaunchTaskParameters launchParameters;
            internal List<LaunchWaypoint> launchWaypoints;
            internal HelicopterLanding deckStop;
            internal double nextDeckEvent;
            internal int unloaded;
            internal bool readyToDepart;
        }

        private sealed class LaunchWaypoint
        {
            private readonly VisualActionTask waypoint;
            private readonly GeoPosition position;
            private readonly Vector3 relativePosition;

            internal LaunchWaypoint(VisualActionTask waypoint)
            {
                this.waypoint = waypoint;
                if (waypoint is GoToRelativePoint relative)
                    relativePosition = relative.relativePosition.value;
                else
                    position = waypoint.WaypointPosition;
            }

            internal bool matches(VisualActionTask current)
            {
                if (!ReferenceEquals(waypoint, current))
                    return false;

                if (current is GoToRelativePoint relative)
                    return relative.relativePosition.value.Equals(relativePosition);

                var currentPosition = current.WaypointPosition;
                return currentPosition.Latitude == position.Latitude && currentPosition.Longitude == position.Longitude &&
                    currentPosition._height == position._height;
            }
        }

        private static bool needsToReturnForFuel(Helicopter helicopter)
        {
            if (helicopter._unlimitedFuel)
                return false;

            var home = helicopter.getHomeBase();
            if (!isAvailableHome(home))
                return false;

            double speed = helicopter._hp._maxSpeedSeaLevel;
            double budget = getFuelBudgetSeconds(helicopter);
            double distance = GeoPosition.HaversineDistanceNmi(helicopter.getGeoPosition(), home.getGeoPosition());
            if (speed <= 0 || !double.IsFinite(speed) || !double.IsFinite(distance) || !double.IsFinite(budget))
                return false;

            double tripSeconds = distance / speed * secondsPerHour + landingSeconds;
            double reserve = Math.Max(minimumReserveSeconds, tripSeconds * reserveFraction);
            return budget <= tripSeconds + reserve + routeCheckIntervalSeconds;
        }

        private static bool isAvailableHome(ObjectBase home)
        {
            return home != null && !home.IsDestroyed && !home.IsDespawned &&
                home._obp._flightDeck != null && !home._obp._flightDeck.DisabledByMission;
        }

        private static int survivorsAboard(Helicopter helicopter)
        {
            return Math.Max(0, helicopter._obp.CurrentTransportedOnBoard.Value - helicopter.EmbarkedGroundPersonnel);
        }

        private static void reportReturningSurvivors(Helicopter helicopter)
        {
            if (helicopter.CurrentOrder.Value?.OrderType != Order.Type.ReturnToBase)
                return;

            int count = survivorsAboard(helicopter);
            if (count > 0)
                reportStatus(helicopter, $"RTB with {survivorCount(count)}.");
        }

        private static void returnHome(int index, string reason)
        {
            var assignment = assignments[index];
            var helicopter = assignment.helicopter;
            var home = helicopter.getHomeBase();
            endSar(index, null, restoreAltitude: false);
            if (!isAvailableHome(home))
            {
                helicopter.ShowOrderFeedback("SAR ended. Home flight deck unavailable", 5f);
                return;
            }

            pruneSkippedRafts(assignment.skippedRafts);
            recoveries.Add(new Recovery
            {
                helicopter = helicopter,
                home = home,
                deck = home._obp._flightDeck,
                waypointTolerance = helicopter._obp._waypointTolerance,
                emcon = helicopter.Emcon.Value,
                weaponStatus = helicopter._weaponStatus,
                survivorRelation = assignment.survivorRelation,
                skippedRafts = assignment.skippedRafts
            });
            if (helicopter.CurrentOrder.Value?.OrderType != Order.Type.ReturnToBase)
                helicopter.setOrder(Order.Type.ReturnToBase, home, displayOrderText: false);
            reportReturningSurvivors(helicopter);
            helicopter.ShowOrderFeedback(reason + ". Returning to base", 5f);
        }

        internal static void updateRecoveries(float realTime)
        {
            if (recoveries.Count == 0)
                return;

            bool checkRecovery = realTime < lastRecoveryCheck || realTime - lastRecoveryCheck >= 0.5f;
            if (checkRecovery)
                lastRecoveryCheck = realTime;

            for (int index = recoveries.Count - 1; index >= 0; index--)
            {
                var recovery = recoveries[index];
                if (recovery.deckStop != null)
                {
                    updateDeckStop(index, recovery);
                    continue;
                }

                if (!checkRecovery)
                    continue;

                if (!isAvailableHome(recovery.home))
                {
                    removeRecovery(index, true);
                    continue;
                }

                if (!recovery.awaitingLaunch)
                {
                    var helicopter = recovery.helicopter;
                    if (helicopter == null || helicopter.IsDestroyed || helicopter.IsDespawned)
                    {
                        removeRecovery(index, false);
                        continue;
                    }

                    var order = helicopter.CurrentOrder.Value;
                    bool redirected = order != null && order.OrderType != Order.Type.None &&
                        order.OrderType != Order.Type.ReturnToBase;
                    bool returnCanceled = (order == null || order.OrderType == Order.Type.None) &&
                        !helicopter._isLanding && !hasActiveDeckTask(recovery.deck, helicopter);
                    if (redirected || returnCanceled)
                    {
                        removeRecovery(index, false);
                        if (canReceiveSarOrders(helicopter))
                            reportStatus(helicopter, abortReport);
                    }
                    continue;
                }

                if (recovery.helicopter != null)
                {
                    resumeAfterTakeoff(index);
                    continue;
                }

                if (recovery.shipLaunch)
                {
                    removeRecovery(index, false);
                    continue;
                }

                var entry = findRecoveryEntry(recovery);
                if (recovery.pending != null)
                {
                    if (recovery.deck.FlightDeckTasks.Contains(recovery.pending))
                        continue;

                    forgetLaunch(recovery);
                    if (!recovery.retryWithRefuel)
                    {
                        removeRecovery(index, false);
                        continue;
                    }

                    recovery.retryWithRefuel = false;
                    recovery.quickTurnaround = false;
                }

                if (entry == null)
                    removeRecovery(index, false);
                else if (entry.Available)
                    queueRecoveredHelicopter(index, entry);
            }
        }

        private static bool hasActiveDeckTask(FlightDeck deck, Helicopter helicopter)
        {
            foreach (var task in deck.FlightDeckTasks)
            {
                if (!task._isCancelled && task.Vehicle == helicopter && !deck._flightDeckTasksToRemove.Contains(task))
                    return true;
            }

            foreach (var task in deck._flightDeckTasksToAdd)
            {
                if (!task._isCancelled && task.Vehicle == helicopter)
                    return true;
            }

            return false;
        }

        private static AircraftRecoveryInventory.Entry findRecoveryEntry(Recovery recovery)
        {
            return recoveryEntries(recovery.deck.RecoveredAircraft).Find(entry => entry.Id == recovery.recoveryId);
        }

        private static bool hasDeckTarget(Recovery recovery, float range)
        {
            if (recovery.topSpeed <= 0 || !double.IsFinite(recovery.topSpeed) ||
                (!recovery.unlimitedFuel && (!double.IsFinite(recovery.fuelBurn) || !float.IsFinite(range))))
                return false;

            var position = recovery.home.getGeoPosition();
            double budget = recovery.unlimitedFuel ? double.PositiveInfinity : range / recovery.fuelBurn;
            return findSarTarget(position, position, recovery.home._taskforce, recovery.survivorRelation, null, recovery.topSpeed,
                budget, recovery.skippedRafts, null, out _, extraDistanceNmi: 2 * departureWaypointNmi) != null;
        }

        private static void queueRecoveredHelicopter(int index, AircraftRecoveryInventory.Entry entry)
        {
            var recovery = recoveries[index];
            if (!hasDeckTarget(recovery, recovery.fullRange))
            {
                removeRecovery(index, false);
                return;
            }

            if (recovery.deck.LaunchesDisabled)
                return;

            recovery.quickTurnaround &= hasDeckTarget(recovery, recovery.remainingRange);
            if (recovery.cooldownSkipped && !recovery.quickTurnaround &&
                (GameTime.missionElapsedTime - recovery.landedAt) *
                Globals._multipliers[OptionsManager.Instance.FlightDeckTimingsMode] < recovery.cooldownDuration)
                return;

            var vehicle = findVehicle(recovery.deck, entry.Type);
            var squadron = findSquadron(vehicle, entry.Squadron);
            var loadout = vehicle?.GetLoadoutByName(recovery.loadoutName);
            if (squadron == null || squadron.Callsigns.Count == 0 || loadout == null)
            {
                recovery.home.ShowOrderFeedback("SAR ended. Rescue loadout unavailable", 5f);
                removeRecovery(index, false);
                return;
            }

            if (squadron.Numbers <= 0 || loadout.Available.Value <= 0 || !recovery.deck.CanLaunchVehicle(loadout, 1))
            {
                recovery.home.ShowOrderFeedback("SAR ended. Flight deck cannot launch the rescue loadout", 5f);
                removeRecovery(index, false);
                return;
            }

            int callsignIndex = Math.Clamp(recovery.callsignIndex, 0, squadron.Callsigns.Count - 1);
            var parameters = new LaunchTaskParameters
            {
                _launchCount = 1,
                _vehicleIndex = recovery.deck._vehiclesOnBoard.IndexOf(vehicle),
                _squadronIndex = vehicle.Squadrons.IndexOf(squadron),
                _loadoutIndex = vehicle.Loadouts.IndexOf(loadout),
                _callsignIndex = callsignIndex,
                _skipReadyUp = recovery.quickTurnaround
            };
            var pending = recovery.deck.createLaunchTask(vehicle, loadout, squadron,
                squadron.Callsigns[callsignIndex], parameters, true, float.NaN);
            if (pending == null)
                return;

            recovery.pending = pending;
            recovery.launchParameters = parameters;
            sarLaunches.Add(parameters, recovery);
            recovery.home.ShowOrderFeedback(recovery.quickTurnaround ? "SAR quick turnaround queued" : "SAR refuel and relaunch queued", 5f);
        }

        private static VehicleTypeOnBoard findVehicle(FlightDeck deck, string type)
        {
            foreach (var vehicle in deck._vehiclesOnBoard)
            {
                if (string.Equals(vehicle._fileName, type, StringComparison.OrdinalIgnoreCase))
                    return vehicle;
            }

            return null;
        }

        private static Squadron findSquadron(VehicleTypeOnBoard vehicle, string name)
        {
            if (vehicle == null)
                return null;

            foreach (var squadron in vehicle.Squadrons)
            {
                if (string.Equals(squadron._name, name, StringComparison.OrdinalIgnoreCase))
                    return squadron;
            }

            return null;
        }

        private static void resumeAfterTakeoff(int index)
        {
            var recovery = recoveries[index];
            var helicopter = recovery.helicopter;
            if (helicopter.IsDestroyed || helicopter.IsDespawned)
            {
                removeRecovery(index, false);
                return;
            }

            if (!helicopter._isInFlight || !helicopter.IsControllable)
                return;

            removeRecovery(index, false);
            if (!canReceiveSarOrders(helicopter))
                return;

            if (!hasNoOrder(helicopter) || hasOtherWaypoints(recovery))
            {
                reportStatus(helicopter, abortReport);
                return;
            }

            pruneSkippedRafts(recovery.skippedRafts);
            var target = findReachableSarTarget(helicopter, recovery.survivorRelation, out _, recovery.skippedRafts);
            if (target != null)
                startSarFlight(helicopter, target, recovery.survivorRelation, recovery.skippedRafts, afterTakeoff: true);
            else
            {
                helicopter.RemoveWaypoints();
                helicopter.setOrder(Order.Type.ReturnToBase, recovery.home, true);
            }
        }

        private static bool hasOtherWaypoints(Recovery recovery)
        {
            var helicopter = recovery.helicopter;
            int count = helicopter.GetNumberOfWaypoints();
            for (int index = 1; index <= count; index++)
            {
                var waypoint = helicopter.GetWaypoint(index);
                if (recovery.launchWaypoints == null || !recovery.launchWaypoints.Exists(launch => launch.matches(waypoint)))
                    return true;
            }

            return false;
        }

        private static void rememberLaunchWaypoints(Recovery recovery)
        {
            var helicopter = recovery.helicopter;
            int count = helicopter.GetNumberOfWaypoints();
            recovery.launchWaypoints = new List<LaunchWaypoint>(count);
            for (int index = 1; index <= count; index++)
                recovery.launchWaypoints.Add(new LaunchWaypoint(helicopter.GetWaypoint(index)));
        }

        private static void forgetLaunch(Recovery recovery)
        {
            if (recovery.launchParameters != null)
                sarLaunches.Remove(recovery.launchParameters);

            recovery.pending = null;
            recovery.launchParameters = null;
        }

        private static void removeRecovery(int index, bool abortLaunch)
        {
            var recovery = recoveries[index];
            if (recovery.deckStop != null)
                endDeckStop(recovery);

            if (abortLaunch && recovery.pending != null && recovery.helicopter == null &&
                recovery.home != null && !recovery.home.IsDestroyed && !recovery.home.IsDespawned &&
                recovery.deck != null && recovery.deck.FlightDeckTasks != null &&
                recovery.deck.FlightDeckTasks.Contains(recovery.pending))
                recovery.deck.abortLaunchTask(recovery.pending._uid, false);

            forgetLaunch(recovery);
            recoveries.RemoveAt(index);
        }

        private static void offloadSurvivors(Recovery recovery, Helicopter helicopter, int survivors)
        {
            if (survivors <= 0)
                return;

            var onBoard = helicopter._obp.CurrentTransportedOnBoard;
            onBoard.Value = Math.Max(0, onBoard.Value - survivors);
            reportOffload(recovery, helicopter, survivors);
        }

        private static void reportOffload(Recovery recovery, Helicopter helicopter, int survivors)
        {
            string helicopterName = System.Security.SecurityElement.Escape(helicopter.Name.Value);
            reportStatus(recovery.home, $"Received {survivorCount(survivors)} from {helicopterName}.");
            recovery.home.ShowOrderFeedback($"SAR dropped off {survivors} survivors", 5f);
        }

        [HarmonyPatch(typeof(HelicopterLanding), "onFixedUpdate")]
        private static class SarTouchdownPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var stopRotors = AccessTools.Method(typeof(HelicopterFlightControlSystem), "stopRotors");
                var touchdown = AccessTools.Method(typeof(SarTouchdownPatch), nameof(handleTouchdown));
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(stopRotors))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, touchdown);
                    }
                    else
                        yield return instruction;
                }
            }

            private static void handleTouchdown(HelicopterFlightControlSystem controls, HelicopterLanding landing)
            {
                if (!beginDeckStop(landing))
                    controls.stopRotors();
            }
        }

        private static bool beginDeckStop(HelicopterLanding landing)
        {
            var task = landing._flightDeckTask;
            if (!(task.Vehicle is Helicopter helicopter) || task._isCancelled ||
                task._forceRecoveryMode != FlightDeckTask.ForceRecoveryMode.None)
                return false;

            var deck = task._flightDeck;
            int index = recoveries.FindIndex(item => item.helicopter == helicopter && item.deck == deck &&
                !item.awaitingLaunch);
            if (index < 0)
                return false;

            var recovery = recoveries[index];
            int survivors = survivorsAboard(helicopter);
            if (survivors == 0 || helicopter.EmbarkedGroundPersonnel >= helicopter._obp._transportCapacity ||
                getDeckDepartureTarget(recovery, task) == null)
                return false;

            recovery.deckStop = landing;
            recovery.nextDeckEvent = GameTime.missionElapsedTime + UnityEngine.Random.Range(5f, 10f) +
                UnityEngine.Random.Range(3f, 5f);
            return true;
        }

        private static ObjectBase getDeckDepartureTarget(Recovery recovery, FlightDeckTask task)
        {
            var helicopter = recovery.helicopter;
            var deck = recovery.deck;
            var order = helicopter.CurrentOrder.Value;
            if (!isAvailableHome(recovery.home) || !canReceiveSarOrders(helicopter) || helicopter._userRoot == null ||
                (order != null && order.OrderType != Order.Type.None && order.OrderType != Order.Type.ReturnToBase) ||
                helicopter.Disabled || helicopter._overallDamage > 0 || helicopter._fuelTanksDropped ||
                deck.LaunchesDisabled || !deck.CanOperateHelicopterInCurrentSeaState(task.VehicleType) ||
                GameTime.missionElapsedTime - deck._lastLaunchTime <= deck._launchDelay)
                return null;

            var position = recovery.home.getGeoPosition();
            double speed = helicopter._hp._maxSpeedSeaLevel;
            double budget = getFuelBudgetSeconds(helicopter);
            if (!double.IsFinite(speed) || speed <= 0 || (!helicopter._unlimitedFuel && !double.IsFinite(budget)))
                return null;

            return findSarTarget(position, position, helicopter._taskforce, recovery.survivorRelation, helicopter,
                speed, budget, recovery.skippedRafts, null, out _, extraDistanceNmi: 2 * departureWaypointNmi);
        }

        private static void endDeckStop(Recovery recovery)
        {
            var landing = recovery.deckStop;
            var helicopter = recovery.helicopter;
            recovery.deckStop = null;
            if (helicopter == null || helicopter.IsDestroyed || helicopter.IsDespawned)
                return;

            if (landing._flightDeckTask?._stateMachine != null &&
                ReferenceEquals(landing._flightDeckTask._stateMachine.CurrentState, landing) &&
                helicopter._hfcs != null)
                helicopter._hfcs.stopRotors();

            if (!recovery.readyToDepart && recovery.unloaded > 0 && isAvailableHome(recovery.home))
                reportOffload(recovery, helicopter, recovery.unloaded);
        }

        private static void updateDeckStop(int index, Recovery recovery)
        {
            var landing = recovery.deckStop;
            var task = landing._flightDeckTask;
            var helicopter = recovery.helicopter;
            if (!isAvailableHome(recovery.home) || helicopter == null || helicopter.IsDestroyed || helicopter.IsDespawned)
            {
                removeRecovery(index, true);
                return;
            }

            if (!ReferenceEquals(task._stateMachine.CurrentState, landing) || task._isCancelled ||
                task._forceRecoveryMode != FlightDeckTask.ForceRecoveryMode.None)
            {
                endDeckStop(recovery);
                return;
            }

            var order = helicopter.CurrentOrder.Value;
            if (order != null && order.OrderType != Order.Type.None && order.OrderType != Order.Type.ReturnToBase)
            {
                removeRecovery(index, false);
                reportStatus(helicopter, abortReport);
                return;
            }

            double now = GameTime.missionElapsedTime;
            while (now >= recovery.nextDeckEvent)
            {
                if (recovery.readyToDepart)
                {
                    var target = getDeckDepartureTarget(recovery, task);
                    if (target == null)
                        endDeckStop(recovery);
                    else
                        departFromDeck(index, recovery, task, target);
                    return;
                }

                if (survivorsAboard(helicopter) > 0)
                {
                    helicopter._obp.CurrentTransportedOnBoard.Value--;
                    recovery.unloaded++;
                }

                if (survivorsAboard(helicopter) > 0)
                    recovery.nextDeckEvent += UnityEngine.Random.Range(3f, 5f);
                else
                {
                    if (recovery.unloaded > 0)
                        reportOffload(recovery, helicopter, recovery.unloaded);
                    recovery.readyToDepart = true;
                    recovery.nextDeckEvent += UnityEngine.Random.Range(2f, 4f);
                }
            }
        }

        private static void departFromDeck(int index, Recovery recovery, FlightDeckTask task, ObjectBase target)
        {
            var helicopter = recovery.helicopter;
            var deck = recovery.deck;
            recovery.deckStop = null;
            removeRecovery(index, false);
            task._recoveryPoint.ReleaseObject(helicopter);
            task._recoveryPoint.Release(helicopter);
            task._elevator?.ReleaseObject(helicopter);
            task._elevator?.Release(helicopter);
            task._stateMachine.setState(new FinishFlightDeckTask(task, true));

            deck._launchedHelicoptersCount++;
            deck._lastLaunchTime = GameTime.missionElapsedTime;
            helicopter._isLanding = false;
            helicopter._isInFlight = true;
            helicopter.GiveControl(recovery.home.getVelocityInKnots());
            helicopter._ignoreInCollisionAvoidanceAfterLaunch = true;
            helicopter.setImmediateFlightConditions();
            helicopter.PlayCodeAnimsForFlight(true);
            helicopter.EnableColliders();
            helicopter.IsControllable = true;
            helicopter._obp._waypointTolerance = recovery.waypointTolerance;
            helicopter._weaponStatus = recovery.weaponStatus;
            helicopter.setEMCON(recovery.emcon, false);
            helicopter._statusInfo = string.Empty;
            pruneSkippedRafts(recovery.skippedRafts);
            startSarFlight(helicopter, target, recovery.survivorRelation, recovery.skippedRafts, afterTakeoff: true);
        }

        [HarmonyPatch(typeof(DeSpawn), "onEnter")]
        private static class SarLandingPatch
        {
            private static void Prefix(DeSpawn __instance, out Recovery __state)
            {
                __state = null;
                var task = __instance._flightDeckTask;
                if (task._forceRecoveryMode == FlightDeckTask.ForceRecoveryMode.DuringLaunch ||
                    !(task.Vehicle is Helicopter helicopter))
                    return;

                var recovery = recoveries.Find(item => !item.awaitingLaunch &&
                    item.helicopter == helicopter && item.deck == task._flightDeck);
                if (recovery == null)
                    return;

                if (recovery.deckStop != null)
                    endDeckStop(recovery);

                recovery.survivors = survivorsAboard(helicopter);
                recovery.loadoutName = helicopter._obp._loadoutVariant;
                recovery.callsignIndex = helicopter._obp._callsignIndex;
                recovery.fullRange = helicopter._hp._maxRangeInKm;
                recovery.remainingRange = Math.Max(0, helicopter.RangeInKm.Value);
                recovery.fuelBurn = getFuelBurnPerSecond(helicopter);
                recovery.topSpeed = helicopter._hp._maxSpeedSeaLevel;
                recovery.unlimitedFuel = helicopter._unlimitedFuel;
                recovery.healthy = !helicopter.Disabled && helicopter._overallDamage <= 0 && !helicopter._fuelTanksDropped &&
                    task._forceRecoveryMode == FlightDeckTask.ForceRecoveryMode.None;
                __state = recovery;
            }

            private static void Postfix(Recovery __state)
            {
                if (__state == null || !__state.helicopter._recoveredByFlightDeck)
                    return;

                var helicopter = __state.helicopter;
                __state.recoveryId = helicopter.AircraftRecoveryId;
                __state.helicopter = null;
                __state.landedAt = GameTime.missionElapsedTime;
                offloadSurvivors(__state, helicopter, __state.survivors);

                foreach (var task in __state.deck._flightDeckTasksToAdd)
                {
                    if (!(task is CoolDownTask cooldown) || cooldown.RecoveryId != __state.recoveryId)
                        continue;

                    __state.cooldownDuration = cooldown._duration;
                    __state.quickTurnaround = __state.healthy && hasDeckTarget(__state, __state.remainingRange);
                    if (__state.quickTurnaround)
                    {
                        __state.cooldownSkipped = true;
                        cooldown._duration = 0;
                    }
                    break;
                }
            }
        }

        [HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.launchVehicle))]
        private static class SarLaunchPatch
        {
            private static void Postfix(LaunchTaskParameters ltp, ObjectBase __result)
            {
                if (ltp != null && __result is Helicopter helicopter && shipLaunches.TryGetValue(ltp, out var launch))
                    trackShipTakeoff(helicopter, launch.order);
            }

            private static bool Prefix(LaunchTaskParameters ltp, out Recovery __state, ref ObjectBase __result)
            {
                __state = launchingRecovery;
                launchingRecovery = null;
                if (ltp != null && shipLaunches.TryGetValue(ltp, out var launch) &&
                    !hasShipSarRaft(launch.order.ship, launch.order.side))
                {
                    __result = null;
                    return false;
                }

                if (ltp == null || !sarLaunches.TryGetValue(ltp, out var recovery))
                    return true;

                var entry = findRecoveryEntry(recovery);
                float range = recovery.quickTurnaround ? recovery.remainingRange : recovery.fullRange;
                if (entry == null || !entry.Available || !hasDeckTarget(recovery, range))
                {
                    recovery.retryWithRefuel = entry?.Available == true && recovery.quickTurnaround &&
                        hasDeckTarget(recovery, recovery.fullRange);
                    __result = null;
                    return false;
                }

                launchingRecovery = recovery;
                return true;
            }

            private static Exception Finalizer(Recovery __state, ObjectBase __result, Exception __exception)
            {
                var recovery = launchingRecovery;
                launchingRecovery = __state;
                if (__exception != null || recovery == null || !(__result is Helicopter helicopter) ||
                    helicopter.AircraftRecoveryId != recovery.recoveryId)
                    return __exception;

                if (recovery.quickTurnaround && !recovery.unlimitedFuel)
                {
                    float fullRange = helicopter._hp._maxRangeInKm;
                    float range = Math.Clamp(recovery.remainingRange, 0, fullRange);
                    float fraction = fullRange > 0 ? range / fullRange : 0;
                    helicopter.RangeInKm.Value = range;
                    helicopter.ActualRangeInKm.Value *= fraction;
                    helicopter._hp._currentFuelMass = helicopter._hp._maxFuel * fraction;
                }

                recovery.helicopter = helicopter;
                forgetLaunch(recovery);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Spawn), "SetupDefaultLaunch")]
        private static class SarDefaultLaunchPatch
        {
            private static void Prefix(FlightDeckTask ____flightDeckTask, out Recovery __state)
            {
                __state = null;
                if (____flightDeckTask.Vehicle is Helicopter helicopter && !helicopter.HasWaypoints())
                    __state = recoveries.Find(item => item.awaitingLaunch && item.helicopter == helicopter);
            }

            private static void Postfix(Recovery __state)
            {
                if (__state != null)
                    rememberLaunchWaypoints(__state);
            }
        }

        [HarmonyPatch(typeof(HelicopterTakeOff), "onExit")]
        private static class SarTakeoffPatch
        {
            private static void Postfix(Helicopter ____helicopter)
            {
                int index = recoveries.FindIndex(item => item.awaitingLaunch && item.helicopter == ____helicopter);
                if (index >= 0 && isAvailableHome(recoveries[index].home))
                    resumeAfterTakeoff(index);
            }
        }

        [HarmonyPatch(typeof(AircraftRecoveryInventory), nameof(AircraftRecoveryInventory.PrepareLaunch))]
        private static class SarRecoveryCreditPatch
        {
            private static bool Prefix(AircraftRecoveryInventory __instance,
                List<AircraftRecoveryInventory.Entry> ___entries, ref AircraftRecoveryInventory.Entry __result)
            {
                var recovery = launchingRecovery;
                if (recovery == null || !ReferenceEquals(__instance, recovery.deck.RecoveredAircraft))
                    return true;

                __result = ___entries.Find(entry => entry.Id == recovery.recoveryId && entry.Available);
                if (__result != null)
                    ___entries.Remove(__result);

                return false;
            }
        }
    }
}
