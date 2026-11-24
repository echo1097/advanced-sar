using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace AdvancedSar
{
    [HarmonyPatch(typeof(ObjectBaseViewModel), nameof(ObjectBaseViewModel.GetContextMenuItems))]
    internal static class SarOrders
    {
        private static readonly List<(Helicopter helicopter, ObjectBase raft, GoToWaypointTask waypoint)> assignments =
            new List<(Helicopter, ObjectBase, GoToWaypointTask)>();

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

            var capacity = helicopter._obp._transportCapacity;
            var onBoard = helicopter._obp.CurrentTransportedOnBoard.Value;
            if (capacity <= 0 || onBoard >= capacity || !helicopter.IsControllable || helicopter._userRoot == null)
            {
                helicopter.ShowOrderFeedback("Unable to comply", 5f);
                VoiceMessage.Instance.PlayVoiceMessage("UnableToComply", helicopter);
                return;
            }

            var target = findSarTarget(helicopter);
            if (target == null)
            {
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
            assignments.Add((helicopter, target, waypoint));
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

            var waypoint = assignments[index].waypoint;
            if (helicopter.CommandPosition is GeoPosition position && position.Equals(waypoint.WaypointPosition))
                helicopter.CommandPosition = null;

            helicopter._userRoot.DeleteTask(waypoint);
            assignments.RemoveAt(index);
            helicopter.ShowOrderFeedback("SAR canceled", 5f);
        }

        internal static void refreshAssignments()
        {
            assignments.RemoveAll(assignment => !hasWaypoint(assignment.helicopter, assignment.waypoint));
        }

        internal static void clearAssignments()
        {
            assignments.Clear();
        }

        private static bool canReceiveSarOrders(Helicopter helicopter)
        {
            return helicopter != null && !helicopter.IsDestroyed && !helicopter.IsDespawned &&
                helicopter._taskforce != null && helicopter.AcceptsOrdersFromPlayer;
        }

        private static bool hasWaypoint(Helicopter helicopter, GoToWaypointTask waypoint)
        {
            if (helicopter == null || helicopter.IsDestroyed || helicopter.IsDespawned || helicopter._userRoot == null)
                return false;

            foreach (var task in helicopter._userRoot.TaskViewModels)
            {
                if (ReferenceEquals(task?.Task, waypoint))
                    return true;
            }

            return false;
        }

        private static ObjectBase findSarTarget(Helicopter helicopter)
        {
            if (!ObjectsManager.InstanceExists(false))
                return null;

            var origin = helicopter.getGeoPosition();
            var taskforce = helicopter._taskforce;
            ObjectBase bestRaft = null;
            int fewestSurvivors = int.MaxValue;
            double bestDistance = double.PositiveInfinity;

            foreach (var raft in ObjectsManager.Instance._listOfAllRafts)
            {
                if (raft == null || raft.IsDestroyed || raft.IsDespawned)
                    continue;

                int survivors = raft._obp.CurrentTransportedOnBoard.Value;
                if (survivors <= 0 || survivors > fewestSurvivors)
                    continue;

                var raftTaskforce = raft._taskforce;
                if (raftTaskforce == null || taskforce.RelationshipTo(raftTaskforce) != RelationsState.Friendly)
                    continue;

                if (assignments.Exists(assignment => assignment.raft == raft))
                    continue;

                var raftPosition = raft.getGeoPosition();
                double distance = GeoPosition.HaversineDistanceNmi(origin, raftPosition);
                if (double.IsNaN(distance))
                    continue;

                if (survivors < fewestSurvivors || distance < bestDistance)
                {
                    bestRaft = raft;
                    fewestSurvivors = survivors;
                    bestDistance = distance;
                }
            }

            return bestRaft;
        }
    }
}
