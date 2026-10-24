using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace StlConverter;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.Begin = lifetime =>
        {
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new ConverterWindow(desktop.Args is { Length: > 0 } a && File.Exists(a[0]) ? a[0] : null, embedded: false);
        };
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // (macOS: OpenGL, which the 3D view draws with, before Metal)
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] })
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
