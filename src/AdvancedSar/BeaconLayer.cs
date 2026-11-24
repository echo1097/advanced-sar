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
        private readonly List<(FrameworkElement element, GeoPosition position)> markers =
            new List<(FrameworkElement, GeoPosition)>();
        private MapControl.MapPanel panel;

        internal BeaconLayer(MapBase map, Brush ink, Brush outline)
        {
            this.map = map;
            this.ink = ink;
            this.outline = outline;
        }

        internal void show(List<GeoPosition> positions)
        {
            if (positions.Count == 0)
            {
                clear();
                return;
            }

            if (panel == null)
            {
                panel = new MapControl.MapPanel
                {
                    ParentMap = map,
                    ClipToBounds = true,
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(panel, 10);
                map.Children.Add(panel);
            }

            while (markers.Count > positions.Count)
            {
                int last = markers.Count - 1;
                panel.Children.Remove(markers[last].element);
                markers.RemoveAt(last);
            }

            for (int index = 0; index < positions.Count; index++)
            {
                var position = positions[index];
                if (index == markers.Count)
                {
                    var element = createRing(12, ink, outline);
                    MapControl.MapPanel.SetAutoCollapse(element, true);
                    MapControl.MapPanel.SetPosition(element, position);
                    panel.Children.Add(element);
                    markers.Add((element, position));
                    continue;
                }

                var marker = markers[index];
                if (marker.position.Latitude == position.Latitude && marker.position.Longitude == position.Longitude)
                    continue;

                MapControl.MapPanel.SetPosition(marker.element, position);
                markers[index] = (marker.element, position);
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

        internal static Grid createRing(int size, Brush ink, Brush outline)
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
