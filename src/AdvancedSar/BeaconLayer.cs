using System.Collections.Generic;
using MapControl;
using Noesis;
using SeaPower;

namespace AdvancedSar
{
    internal sealed class BeaconLayer
    {
        private const int markerSize = 12;
        private const int tooltipGap = 4;

        private readonly MapBase map;
        private readonly Brush outline;
        private readonly List<(Grid element, ToolTip tooltip, GeoPosition position, int survivors, Ellipse ring, Ellipse dot, Brush color)> markers =
            new List<(Grid, ToolTip, GeoPosition, int, Ellipse, Ellipse, Brush)>();
        private MapControl.MapPanel panel;

        internal BeaconLayer(MapBase map, Brush outline)
        {
            this.map = map;
            this.outline = outline;
        }

        internal void show(List<(GeoPosition position, int survivors, Brush color)> beacons)
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
                var (position, survivors, color) = beacons[index];
                if (index == markers.Count)
                {
                    var element = createRing(markerSize, color, outline, out var ring, out var dot);
                    element.Background = Brushes.Transparent;
                    element.IsHitTestVisible = true;

                    var tooltip = new ToolTip { IsHitTestVisible = false, Content = formatSurvivors(survivors) };
                    ToolTipService.SetToolTip(element, tooltip);
                    ToolTipService.SetPlacement(element, PlacementMode.Top);
                    int targetSize = markerSize + tooltipGap * 2;
                    ToolTipService.SetPlacementRectangle(element, new Rect(-tooltipGap, -tooltipGap, targetSize, targetSize));
                    ToolTipService.SetInitialShowDelay(element, 0);
                    ToolTipService.SetShowDuration(element, int.MaxValue);

                    MapControl.MapPanel.SetAutoCollapse(element, true);
                    MapControl.MapPanel.SetPosition(element, position);
                    panel.Children.Add(element);
                    markers.Add((element, tooltip, position, survivors, ring, dot, color));
                    continue;
                }

                var marker = markers[index];
                if (marker.position.Latitude != position.Latitude || marker.position.Longitude != position.Longitude)
                    MapControl.MapPanel.SetPosition(marker.element, position);

                if (marker.survivors != survivors)
                    marker.tooltip.Content = formatSurvivors(survivors);

                if (marker.color != color)
                {
                    marker.ring.Stroke = color;
                    marker.dot.Fill = color;
                    marker.color = color;
                }

                marker.position = position;
                marker.survivors = survivors;
                markers[index] = marker;
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

        internal static Grid createRing(int size, Brush ink, Brush outline, out Ellipse ring, out Ellipse dot)
        {
            var glyph = new Grid
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            glyph.Children.Add(new Ellipse { Stroke = outline, StrokeThickness = 4 });
            ring = new Ellipse
            {
                Margin = new Thickness(1),
                Stroke = ink,
                StrokeThickness = 1.5f
            };
            dot = new Ellipse
            {
                Width = 3,
                Height = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = ink
            };

            glyph.Children.Add(ring);
            glyph.Children.Add(dot);

            return glyph;
        }
    }
}
