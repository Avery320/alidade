using System;
using System.Drawing;

namespace Torquetum.Nuncius;

internal static class ComponentIcons
{
    internal static Bitmap Load(string name)
    {
        using var stream = typeof(ComponentIcons).Assembly.GetManifestResourceStream(typeof(ComponentIcons), $"Icons.{name}.png")
            ?? throw new InvalidOperationException($"Missing Nuncius component icon: {name}");
        // Rhino supplies the Grasshopper System.Drawing implementation on macOS.
#pragma warning disable CA1416
        using var bitmap = new Bitmap(stream);
        return (Bitmap)bitmap.Clone();
#pragma warning restore CA1416
    }
}
