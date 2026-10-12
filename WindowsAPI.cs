using System.Runtime.InteropServices;

readonly record struct ScreenBounds(double Left, double Top, double Width, double Height)
{
    public double CenterX => Left + Width / 2.0;
    public double CenterY => Top + Height / 2.0;

    // Màn hình chính, pixel vật lý (đã bật DPI aware)
    public static ScreenBounds GetPrimaryScreenBounds() =>
        new(0, 0, WindowsApi.GetSystemMetrics(0), WindowsApi.GetSystemMetrics(1));
}

static class WindowsApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
}