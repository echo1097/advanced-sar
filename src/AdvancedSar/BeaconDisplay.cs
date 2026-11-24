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

        private readonly Dictionary<MapDrawToolbarView, (ToolBar toolbar, Button button, MapViewModel model)> buttons =
            new Dictionary<MapDrawToolbarView, (ToolBar, Button, MapViewModel)>();
        private readonly Dictionary<MapBase, (MapViewModel model, BeaconLayer layer)> maps =
            new Dictionary<MapBase, (MapViewModel, BeaconLayer)>();
        private readonly List<GeoPosition> positions = new List<GeoPosition>();
        private readonly ManualLogSource logger;
        private readonly SolidColorBrush inactiveBrush = new SolidColorBrush(Color.FromArgb(255, 180, 184, 186));
        private readonly SolidColorBrush activeBrush = new SolidColorBrush(Color.FromArgb(255, 255, 216, 141));
        private readonly SolidColorBrush darkBrush = new SolidColorBrush(Color.FromArgb(255, 44, 49, 52));
        private UnityEngine.Texture2D toolbarTexture;
        private ImageBrush toolbarMask;
        private bool showBeacons;
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

            if (toolbarMask == null)
                loadToolbarIcon();

            var button = new Button
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
                    OpacityMask = toolbarMask
                }
            };
            button.Click += toggleBeacons;
            updateButton(button);
            toolbar.Items.Add(button);
            buttons.Add(view, (toolbar, button, scene.DisplayMap));
        }

        internal void removeToolbar(MapDrawToolbarView view)
        {
            if (!buttons.TryGetValue(view, out var entry))
                return;

            removeButton(entry.toolbar, entry.button);
            buttons.Remove(view);
        }

        internal void setMap(MapBase map, MapViewModel model)
        {
            if (maps.TryGetValue(map, out var current))
            {
                if (ReferenceEquals(current.model, model))
                    return;

                current.layer.clear();
                maps.Remove(map);
            }

            if (model != null)
                maps.Add(map, (model, new BeaconLayer(map, activeBrush, darkBrush)));
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
                updateButton(entry.button);

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

        private void refreshVisibleMaps()
        {
            bool positionsRead = false;
            foreach (var pair in maps)
            {
                var entry = pair.Value;
                if (!pair.Key.IsVisible || !hasToolbar(entry.model))
                {
                    entry.layer.clear();
                    continue;
                }

                if (!positionsRead)
                {
                    readPositions();
                    positionsRead = true;
                }

                entry.layer.show(positions);
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

        private void readPositions()
        {
            positions.Clear();
            if (!ObjectsManager.InstanceExists(false))
                return;

            foreach (var raft in ObjectsManager.Instance._listOfAllRafts)
            {
                if (raft != null && !raft.IsDestroyed)
                    positions.Add(raft.getGeoPosition());
            }
        }

        private void updateButton(Button button)
        {
            var brush = showBeacons ? activeBrush : inactiveBrush;
            if (toolbarMask != null && button.Content is Border icon)
                icon.Background = brush;
            else
                button.Content = BeaconLayer.createRing(16, brush, darkBrush);

            ToolTipService.SetToolTip(button, showBeacons ? "Hide rescue beacons" : "Show all rescue beacons");
        }

        private void loadToolbarIcon()
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

            toolbarTexture = texture;
            toolbarMask = new ImageBrush(new TextureSource(texture));
        }

        private void removeButton(ToolBar toolbar, Button button)
        {
            button.Click -= toggleBeacons;
            toolbar.Items.Remove(button);
        }

        public void Dispose()
        {
            foreach (var entry in buttons.Values)
                removeButton(entry.toolbar, entry.button);

            foreach (var entry in maps.Values)
                entry.layer.clear();

            buttons.Clear();
            maps.Clear();

            if (toolbarTexture != null)
                UnityEngine.Object.Destroy(toolbarTexture);
        }
    }
}
