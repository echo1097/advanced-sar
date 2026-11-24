using System.Collections.Generic;
using MapControl;
using Noesis;
using SeaPower;

namespace AdvancedSar
{
    internal sealed class BeaconLayer
    {
        private readonly MapBase map;
        private readonly Brush ink;
        private readonly Brush outline;
        private readonly List<(Grid element, ToolTip tooltip, GeoPosition position, int survivors)> markers =
            new List<(Grid, ToolTip, GeoPosition, int)>();
        private MapControl.MapPanel panel;

        internal BeaconLayer(MapBase map, Brush ink, Brush outline)
        {
            this.map = map;
            this.ink = ink;
            this.outline = outline;
        }

        internal void show(List<(GeoPosition position, int survivors)> beacons, ImageBrush iconMask)
        {
            if (beacons.Count == 0)
            {
                clear();
                return;
            }

            if (panel == null)
            {
                panel = new MapControl.MapPanel
                {
                    ParentMap = map,
                    ClipToBounds = true
                };
                Panel.SetZIndex(panel, 10);
                map.Children.Add(panel);
            }

            while (markers.Count > beacons.Count)
            {
                int last = markers.Count - 1;
                panel.Children.Remove(markers[last].element);
                markers.RemoveAt(last);
            }

            for (int index = 0; index < beacons.Count; index++)
            {
                var (position, survivors) = beacons[index];
                if (index == markers.Count)
                {
                    var element = createRing(16, ink, outline, iconMask);
                    element.Background = Brushes.Transparent;
                    element.IsHitTestVisible = true;

                    var tooltip = new ToolTip { IsHitTestVisible = false, Content = formatSurvivors(survivors) };
                    ToolTipService.SetToolTip(element, tooltip);
                    ToolTipService.SetPlacement(element, PlacementMode.Center);
                    ToolTipService.SetInitialShowDelay(element, 0);
                    ToolTipService.SetShowDuration(element, int.MaxValue);

                    MapControl.MapPanel.SetAutoCollapse(element, true);
                    MapControl.MapPanel.SetPosition(element, position);
                    panel.Children.Add(element);
                    markers.Add((element, tooltip, position, survivors));
                    continue;
                }

                var marker = markers[index];
                if (marker.position.Latitude != position.Latitude || marker.position.Longitude != position.Longitude)
                    MapControl.MapPanel.SetPosition(marker.element, position);

                if (marker.survivors != survivors)
                    marker.tooltip.Content = formatSurvivors(survivors);

                markers[index] = (marker.element, marker.tooltip, position, survivors);
            }
        }

        internal void clear()
        {
            if (panel == null)
                return;

            map.Children.Remove(panel);
            panel = null;
            markers.Clear();
        }

        private static string formatSurvivors(int survivors)
        {
            string label = survivors == 1 ? "survivor" : "survivors";
            return $"Life raft - {survivors} {label}";
        }

        internal static Grid createRing(int size, Brush ink, Brush outline, ImageBrush iconMask = null)
        {
            var glyph = new Grid
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            if (iconMask != null)
            {
                glyph.Children.Add(new Border { Background = ink, OpacityMask = iconMask });
                return glyph;
            }

            glyph.Children.Add(new Ellipse { Stroke = outline, StrokeThickness = 4 });
            glyph.Children.Add(new Ellipse
            {
                Margin = new Thickness(1),
                Stroke = ink,
                StrokeThickness = 1.5f
            });
            glyph.Children.Add(new Ellipse
            {
                Width = 3,
                Height = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = ink
            });

            return glyph;
        }
    }
}
