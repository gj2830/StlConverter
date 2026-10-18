using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StlConverter;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ConverterWindow(desktop.Args is { Length: > 0 } args && File.Exists(args[0]) ? args[0] : null, embedded: false);
        base.OnFrameworkInitializationCompleted();
    }
}
