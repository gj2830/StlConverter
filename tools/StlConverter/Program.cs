using Avalonia;

namespace StlConverter;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // (macOS: OpenGL, which the 3D view draws with, before Metal)
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] })
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
}
