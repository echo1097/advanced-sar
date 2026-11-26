using System.Collections.Generic;
using SeaPower;

namespace AdvancedSar
{
    internal static partial class SarOrders
    {
        private sealed class ShipSarOrder
        {
            internal ObjectBase ship;
            internal RelationsState side;
        }

        private sealed class ShipLaunch
        {
            internal ShipSarOrder order;
            internal FlightDeck deck;
            internal PendingLaunchTask pending;
        }

        private static readonly Dictionary<LaunchTaskParameters, ShipLaunch> shipLaunches =
            new Dictionary<LaunchTaskParameters, ShipLaunch>();
        private static readonly Dictionary<CoolDownTask, ShipSarOrder> coolingHelicopters =
            new Dictionary<CoolDownTask, ShipSarOrder>();

        private static void clearShipOrders()
        {
            coolingHelicopters.Clear();
            shipLaunches.Clear();
        }

        private static bool hasShipSarRaft(ObjectBase ship, RelationsState side)
        {
            if (ship == null || ship._taskforce == null || !ObjectsManager.InstanceExists(false))
                return false;

            foreach (var raft in ObjectsManager.Instance._listOfAllRafts)
            {
                if (getEligibleSurvivorCount(raft, ship._taskforce, side) > 0)
                    return true;
            }
            return false;
        }

        private static void beginShipSar(ObjectBase ship, RelationsState side)
        {
            if (!hasShipSarRaft(ship, side))
            {
                ship.ShowOrderFeedback($"No {survivorSide(side)} life rafts with survivors", 5f);
                return;
            }

            refreshAssignments();
            int previousAssignments = assignments.Count;
            foreach (var aircraft in ObjectsManager.Instance._listOfAllAircraftObjects)
            {
                if (aircraft is Helicopter helicopter && helicopter.getHomeBase() == ship &&
                    helicopter._isInFlight && !helicopter._isLanding && canReceiveSarOrders(helicopter) &&
                    helicopter.IsControllable && hasRoom(helicopter) &&
                    !assignments.Exists(item => item.helicopter == helicopter) &&
                    !recoveries.Exists(item => item.helicopter == helicopter))
                    beginSar(helicopter, side);
            }
            int started = assignments.Count - previousAssignments;

            var deck = ship._obp._flightDeck;
            var order = new ShipSarOrder { ship = ship, side = side };
            int queued = 0;
            int cooling = 0;
            if (!deck.LaunchesDisabled)
            {
                var tasks = new List<FlightDeckTask>(deck.FlightDeckTasks);
                tasks.AddRange(deck._flightDeckTasksToAdd);
                foreach (var task in tasks)
                {
                    if (task._isCancelled || deck._flightDeckTasksToRemove.Contains(task))
                        continue;

                    if (task is CoolDownTask cooldown)
                    {
                        var vehicle = findVehicle(deck, cooldown.iniRef);
                        if (vehicle?._type == VehicleType.Helicopter && PatrolZone.IsSARHelicopter(vehicle) &&
                            !recoveries.Exists(item => item.deck == deck && item.recoveryId != null &&
                                item.recoveryId == cooldown.RecoveryId) &&
                            coolingHelicopters.TryAdd(cooldown, order))
                            cooling++;
                    }
                    else if (task is PendingLaunchTask pending)
                        queued += preparePendingLaunch(deck, pending, order);
                    else if (task is LaunchTask && task.Vehicle is Helicopter launching && !launching._isInFlight &&
                        hasRoom(launching) && hasNoOrder(launching) &&
                        !recoveries.Exists(item => item.helicopter == launching))
                    {
                        var departure = trackShipTakeoff(launching, order);
                        rememberLaunchWaypoints(departure);
                        queued++;
                    }
                }

                foreach (var vehicle in deck._vehiclesOnBoard)
                {
                    if (vehicle._type != VehicleType.Helicopter)
                        continue;

                    foreach (var squadron in vehicle.Squadrons)
                    {
                        int available = squadron.Numbers;
                        foreach (var recovery in recoveries)
                        {
                            if (recovery.deck != deck || recovery.pending != null || recovery.recoveryId == null)
                                continue;
                            var entry = findRecoveryEntry(recovery);
                            if (entry?.Available == true && entry.Type == vehicle._fileName && entry.Squadron == squadron._name)
                                available--;
                        }

                        for (int index = 0; index < available; index++)
                        {
                            if (!queueShipHelicopter(deck, vehicle, squadron, order))
                                break;
                            queued++;
                        }
                    }
                }
            }

            ship.ShowOrderFeedback($"SAR: {started} started, {queued} queued, {cooling} awaiting servicing", 5f);
        }

        private static Recovery trackShipTakeoff(Helicopter helicopter, ShipSarOrder order)
        {
            var departure = new Recovery
            {
                shipLaunch = true,
                helicopter = helicopter,
                home = order.ship,
                deck = order.ship._obp._flightDeck,
                survivorRelation = order.side
            };
            recoveries.Add(departure);
            return departure;
        }

        private static int preparePendingLaunch(FlightDeck deck, PendingLaunchTask pending, ShipSarOrder order)
        {
            var vehicle = pending.VehicleType;
            var parameters = pending._ltp;
            if (vehicle?._type != VehicleType.Helicopter || pending.LaunchCount <= 0 || parameters == null ||
                shipLaunches.ContainsKey(parameters) || sarLaunches.ContainsKey(parameters))
                return 0;

            var current = vehicle.Loadouts[pending._loadoutIndex];
            var best = bestLoadout(deck, vehicle, current);
            if (best == null)
                return 0;

            if (best == current && parameters._missionType == FlightDeckTask.MissionType.None &&
                parameters._groundCargo.Count == 0 && parameters._waypoints.Count == 0 &&
                string.IsNullOrEmpty(parameters._patrolZoneID))
            {
                shipLaunches.Add(parameters, new ShipLaunch
                {
                    order = order,
                    deck = deck,
                    pending = pending
                });
                pending.launchAllowed = true;
                return pending.LaunchCount;
            }

            deck.abortLaunchTask(pending._uid, false);
            return 0;
        }

        private static Loadout bestLoadout(FlightDeck deck, VehicleTypeOnBoard vehicle, Loadout current = null)
        {
            Loadout best = null;
            int bestSeats = 0;
            int bestRank = 0;
            foreach (var loadout in vehicle.Loadouts)
            {
                int rank = loadoutRank(loadout.Name);
                if (rank == 0 || (loadout != current && (loadout.Available.Value <= 0 || !deck.CanLaunchVehicle(loadout, 1))))
                    continue;

                int seats = PatrolZone.GetSARTransportCapacity(loadout);
                if (loadout == current)
                    rank = int.MaxValue;
                if (seats > 0 && (seats > bestSeats || (seats == bestSeats && rank > bestRank)))
                {
                    best = loadout;
                    bestSeats = seats;
                    bestRank = rank;
                }
            }
            return best;
        }

        private static int loadoutRank(string name)
        {
            switch (name.Replace(" ", "").Replace("-", "").Replace("_", "").Replace("&", "and").ToLowerInvariant())
            {
                case "sar":
                case "csar":
                case "searchandrescue":
                case "combatsearchandrescue":
                    return 3;
                case "passengers":
                case "transport":
                    return 2;
                case "empty":
                case "ferry":
                    return 1;
                default:
                    return 0;
            }
        }

        private static bool queueShipHelicopter(FlightDeck deck, VehicleTypeOnBoard vehicle, Squadron squadron, ShipSarOrder order)
        {
            if (squadron == null || squadron.Numbers <= 0 || squadron.Callsigns.Count == 0)
                return false;

            var loadout = bestLoadout(deck, vehicle);
            if (loadout == null)
                return false;

            var parameters = new LaunchTaskParameters
            {
                _vehicleIndex = deck._vehiclesOnBoard.IndexOf(vehicle),
                _squadronIndex = vehicle.Squadrons.IndexOf(squadron),
                _callsignIndex = 0,
                _loadoutIndex = vehicle.Loadouts.IndexOf(loadout),
                _launchCount = 1
            };
            var pending = deck.createLaunchTask(vehicle, loadout, squadron, squadron.Callsigns[0], parameters, true, float.NaN);
            if (pending == null)
                return false;

            shipLaunches.Add(parameters, new ShipLaunch
            {
                order = order,
                deck = deck,
                pending = pending
            });
            return true;
        }

        internal static void updateShipLaunches()
        {
            if (shipLaunches.Count == 0)
                return;

            if (!ObjectsManager.InstanceExists(false))
            {
                shipLaunches.Clear();
                return;
            }

            foreach (var item in shipLaunches)
            {
                var launch = item.Value;
                if (isAvailableHome(launch.order.ship) && !launch.pending._isCancelled &&
                    launch.pending.LaunchCount > 0 &&
                    (launch.deck.FlightDeckTasks.Contains(launch.pending) ||
                        launch.deck._flightDeckTasksToAdd.Contains(launch.pending)))
                    continue;

                shipLaunches.Remove(item.Key);
                break;
            }
        }

        internal static void updateCoolingHelicopters()
        {
            if (coolingHelicopters.Count == 0)
                return;

            foreach (var item in coolingHelicopters)
            {
                var task = item.Key;
                var order = item.Value;
                var deck = task._flightDeck;
                if (isAvailableHome(order.ship) && order.ship.AcceptsOrdersFromPlayer && !task._isCancelled)
                {
                    if (deck.FlightDeckTasks.Contains(task) || deck._flightDeckTasksToAdd.Contains(task))
                        continue;

                    var vehicle = findVehicle(deck, task.iniRef);
                    if (!deck.LaunchesDisabled && vehicle != null && hasShipSarRaft(order.ship, order.side))
                        queueShipHelicopter(deck, vehicle, findSquadron(vehicle, task.squadronRef), order);
                }
                coolingHelicopters.Remove(task);
                break;
            }
        }
    }
}
