using BepInEx;
using HarmonyLib;
using MapControl;
using Noesis;
using SeapowerUI;
using UnityEngine;

namespace AdvancedSar
{
    [BepInPlugin(PluginId, "Advanced SAR - Search & Rescue", "0.4.2")]
    public sealed class AdvancedSarPlugin : BaseUnityPlugin
    {
        private const string PluginId = "com.advancedsar.seapower";

        private static BeaconDisplay display;
        private Harmony harmony;

        private void OnEnable()
        {
            display = new BeaconDisplay(Logger);
            harmony = new Harmony(PluginId);
            harmony.PatchAll(typeof(AdvancedSarPlugin).Assembly);
            Logger.LogInfo("Advanced SAR loaded.");
        }

        private void Update()
        {
            display?.refresh(Time.unscaledTime);
            SarOrders.updateRoutes(Time.unscaledTime);
        }

        private void OnDisable()
        {
            harmony?.UnpatchSelf();
            SarOrders.clearAssignments();
            display?.Dispose();
            display = null;
        }

        [HarmonyPatch(typeof(MapDrawToolbarView), MethodType.Constructor)]
        private static class ToolbarPatch
        {
            private static void Postfix(MapDrawToolbarView __instance)
            {
                __instance.Loaded += onToolbarLoaded;
                __instance.Unloaded += onToolbarUnloaded;
            }

            private static void onToolbarLoaded(object sender, RoutedEventArgs args)
            {
                display?.addToolbar((MapDrawToolbarView)sender);
            }

            private static void onToolbarUnloaded(object sender, RoutedEventArgs args)
            {
                display?.removeToolbar((MapDrawToolbarView)sender);
            }
        }

        [HarmonyPatch(typeof(MapBase), "Present")]
        private static class MapPatch
        {
            private static void Postfix(MapBase __instance, MapViewModel __0)
            {
                display?.setMap(__instance, __0);
            }
        }
    }
}
