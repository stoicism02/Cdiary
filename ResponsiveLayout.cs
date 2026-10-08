namespace Cdiary;

public enum WindowLayoutMode { Compact, Standard, Wide }

public static class ResponsiveLayout
{
    // WPF logical units (DIPs), independent of the monitor's pixel density.
    public static WindowLayoutMode ForWidth(double width) => width < 1000
        ? WindowLayoutMode.Compact : width < 1400 ? WindowLayoutMode.Standard : WindowLayoutMode.Wide;
    public static int GalleryColumns(double availableWidth) => Math.Clamp((int)(availableWidth / 250), 2, 5);
}
