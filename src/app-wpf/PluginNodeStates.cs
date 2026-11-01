using System.Windows;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

internal static class PluginNodeStates
{
    // Deactivate = powered off + bypassed. The realtime engine skips the plugin's process call when a
    // slot is unpowered, and its bypass branch still copies input to output, so audio keeps flowing
    // through the chain while the plugin uses no DSP time.
    public static bool IsDeactivated(PluginNodeSnapshot node)
    {
        return !node.MissingPlugin && !node.Enabled && node.Bypassed;
    }

    // Grey with light diagonal stripes: distinct from Off (dark stripes) and Bypass (orange stripes).
    public static Brush DeactivatedBrush()
    {
        const double tileSize = 18.0;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x48)),
            null,
            new RectangleGeometry(new Rect(0, 0, tileSize, tileSize))));
        group.Children.Add(new GeometryDrawing(
            null,
            new Pen(new SolidColorBrush(Color.FromArgb(150, 154, 168, 178)), 6.0),
            new LineGeometry(new Point(0, tileSize), new Point(tileSize, 0))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, tileSize, tileSize),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, tileSize, tileSize),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }
}
