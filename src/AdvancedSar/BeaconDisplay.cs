using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using MapControl;
using Noesis;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace AdvancedSar
{
    internal sealed class BeaconDisplay : IDisposable
    {
        private const float refreshInterval = 0.25f;

        private readonly Dictionary<MapDrawToolbarView, (ToolBar toolbar, Button beaconButton, Button enemyButton, MapViewModel model)> buttons =
            new Dictionary<MapDrawToolbarView, (ToolBar, Button, Button, MapViewModel)>();
        private readonly Dictionary<MapBase, (MapViewModel model, BeaconLayer layer)> maps =
            new Dictionary<MapBase, (MapViewModel, BeaconLayer)>();
        private readonly List<(GeoPosition position, int survivors, Brush color)> beacons =
            new List<(GeoPosition, int, Brush)>();
        private readonly ManualLogSource logger;
        private readonly SolidColorBrush inactiveBrush = new SolidColorBrush(Color.FromArgb(255, 180, 184, 186));
        private readonly SolidColorBrush activeBrush = new SolidColorBrush(Color.FromArgb(255, 255, 216, 141));
        private readonly SolidColorBrush friendlyBrush = new SolidColorBrush(Color.FromArgb(255, 111, 181, 255));
        private readonly SolidColorBrush enemyBrush = new SolidColorBrush(Color.FromArgb(255, 255, 112, 101));
        private readonly SolidColorBrush darkBrush = new SolidColorBrush(Color.FromArgb(255, 44, 49, 52));
        private UnityEngine.Texture2D iconTexture;
        private ImageBrush iconMask;
        private bool showBeacons;
        private bool showEnemyBeacons = true;
        private float nextRefresh;

        internal BeaconDisplay(ManualLogSource logger)
        {
            this.logger = logger;
        }

        internal void addToolbar(MapDrawToolbarView view)
        {
            if (buttons.ContainsKey(view) || !(view.DataContext is SceneMapViewModel scene))
                return;

            if (!(view.Content is ToolBar toolbar))
            {
                logger.LogWarning("Advanced SAR could not find the tactical map toolbar.");
                return;
            }

            if (iconMask == null)
                loadIcon();

            var beaconButton = new Button
            {
                Width = 30,
                Height = 30,
                Focusable = false,
                Content = new Border
                {
                    Width = 20,
                    Height = 20,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    OpacityMask = iconMask
                }
            };
            var enemyButton = new Button
            {
                Width = 48,
                Height = 30,
                Focusable = false,
                Content = "Enemy"
            };

            beaconButton.Click += toggleBeacons;
            enemyButton.Click += toggleEnemyBeacons;
            updateButtons(beaconButton, enemyButton);
            toolbar.Items.Add(beaconButton);
            toolbar.Items.Add(enemyButton);
            buttons.Add(view, (toolbar, beaconButton, enemyButton, scene.DisplayMap));
        }

        internal void removeToolbar(MapDrawToolbarView view)
        {
            if (!buttons.TryGetValue(view, out var entry))
                return;

            removeButtons(entry.toolbar, entry.beaconButton, entry.enemyButton);
            buttons.Remove(view);
        }

        internal void setMap(MapBase map, MapViewModel model)
        {
            if (maps.TryGetValue(map, out var current))
            {
                if (ReferenceEquals(current.model, model))
                    return;

                removeMap(map);
            }

            if (model != null)
            {
                map.Unloaded += onMapUnloaded;
                maps.Add(map, (model, new BeaconLayer(map, darkBrush)));
            }
        }

        internal void pruneStale()
        {
            var staleMaps = new List<MapBase>();
            foreach (var map in maps.Keys)
            {
                if (!map.IsLoaded)
                    staleMaps.Add(map);
            }

            foreach (var map in staleMaps)
                removeMap(map);

            var staleToolbars = new List<MapDrawToolbarView>();
            foreach (var view in buttons.Keys)
            {
                if (!view.IsLoaded)
                    staleToolbars.Add(view);
            }

            foreach (var view in staleToolbars)
                removeToolbar(view);
        }

        private void onMapUnloaded(object sender, RoutedEventArgs args)
        {
            removeMap((MapBase)sender);
        }

        private void removeMap(MapBase map)
        {
            if (!maps.TryGetValue(map, out var entry))
                return;

            map.Unloaded -= onMapUnloaded;
            entry.layer.clear();
            maps.Remove(map);
        }

        internal void refresh(float time)
        {
            if (!showBeacons || time < nextRefresh)
                return;

            nextRefresh = time + refreshInterval;
            refreshVisibleMaps();
        }

        private void toggleBeacons(object sender, RoutedEventArgs args)
        {
            showBeacons = !showBeacons;
            foreach (var entry in buttons.Values)
                updateButtons(entry.beaconButton, entry.enemyButton);

            if (showBeacons)
            {
                nextRefresh = UnityEngine.Time.unscaledTime + refreshInterval;
                refreshVisibleMaps();
            }
            else
            {
                foreach (var entry in maps.Values)
                    entry.layer.clear();
            }
        }

        private void toggleEnemyBeacons(object sender, RoutedEventArgs args)
        {
            showEnemyBeacons = !showEnemyBeacons;
            foreach (var entry in buttons.Values)
                updateButtons(entry.beaconButton, entry.enemyButton);

            if (showBeacons)
                refreshVisibleMaps();
        }

        private void refreshVisibleMaps()
        {
            bool beaconsRead = false;
            foreach (var pair in maps)
            {
                var entry = pair.Value;
                if (!pair.Key.IsVisible || !hasToolbar(entry.model))
                {
                    entry.layer.clear();
                    continue;
                }

                if (!beaconsRead)
                {
                    readBeacons();
                    beaconsRead = true;
                }

                entry.layer.show(beacons);
            }
        }

        private bool hasToolbar(MapViewModel model)
        {
            foreach (var entry in buttons.Values)
            {
                if (ReferenceEquals(entry.model, model))
                    return true;
            }

            return false;
        }

        private void readBeacons()
        {
            beacons.Clear();
            if (!ObjectsManager.InstanceExists(false))
                return;

            var player = Globals._playerTaskforce;
            foreach (var raft in ObjectsManager.Instance._listOfAllRafts)
            {
                if (raft == null || raft.IsDestroyed || raft.IsDespawned)
                    continue;

                var side = raft._taskforce;
                var relation = side == null || player == null ? RelationsState.Unknown : player.RelationshipTo(side);
                if (relation == RelationsState.Hostile && !showEnemyBeacons)
                    continue;

                Brush color = relation switch
                {
                    RelationsState.Friendly => friendlyBrush,
                    RelationsState.Hostile => enemyBrush,
                    _ => activeBrush
                };

                beacons.Add((raft.getGeoPosition(), raft._obp.CurrentTransportedOnBoard.Value, color));
            }
        }

        private void updateButtons(Button beaconButton, Button enemyButton)
        {
            var brush = showBeacons ? activeBrush : inactiveBrush;
            if (iconMask != null && beaconButton.Content is Border icon)
                icon.Background = brush;
            else
                beaconButton.Content = BeaconLayer.createRing(16, brush, darkBrush, out _, out _);

            ToolTipService.SetToolTip(beaconButton, showBeacons ? "Hide rescue beacons" : "Show rescue beacons");

            enemyButton.IsEnabled = showBeacons;
            enemyButton.Foreground = showBeacons && showEnemyBeacons ? enemyBrush : inactiveBrush;
            ToolTipService.SetToolTip(enemyButton, showEnemyBeacons ? "Hide enemy survivors" : "Show enemy survivors");
        }

        private void loadIcon()
        {
            using var asset = typeof(BeaconDisplay).Assembly.GetManifestResourceStream("AdvancedSar.LifeRing.png");
            if (asset == null)
                return;

            using var data = new MemoryStream();
            asset.CopyTo(data);

            var texture = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, true)
            {
                filterMode = UnityEngine.FilterMode.Trilinear,
                wrapMode = UnityEngine.TextureWrapMode.Clamp
            };

            if (!UnityEngine.ImageConversion.LoadImage(texture, data.ToArray(), true))
            {
                UnityEngine.Object.Destroy(texture);
                logger.LogWarning("Advanced SAR could not decode the life ring icon.");
                return;
            }

            iconTexture = texture;
            iconMask = new ImageBrush(new TextureSource(texture));
        }

        private void removeButtons(ToolBar toolbar, Button beaconButton, Button enemyButton)
        {
            beaconButton.Click -= toggleBeacons;
            enemyButton.Click -= toggleEnemyBeacons;
            toolbar.Items.Remove(beaconButton);
            toolbar.Items.Remove(enemyButton);
        }

        public void Dispose()
        {
            foreach (var entry in buttons.Values)
                removeButtons(entry.toolbar, entry.beaconButton, entry.enemyButton);

            foreach (var pair in maps)
            {
                pair.Key.Unloaded -= onMapUnloaded;
                pair.Value.layer.clear();
            }

            buttons.Clear();
            maps.Clear();
            beacons.Clear();

            if (iconTexture != null)
                UnityEngine.Object.Destroy(iconTexture);

            iconTexture = null;
            iconMask = null;
        }
    }
}
