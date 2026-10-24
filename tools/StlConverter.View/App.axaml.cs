using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StlConverter;

public partial class App : Application
{
    /// <summary>What the system starts with, set by its Program before Avalonia starts: the desktop opens a window
    /// around the view, the browser puts the view in the page.</summary>
    public static Action<IApplicationLifetime?> Begin { get; set; } = _ => { };

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Begin(ApplicationLifetime);
        base.OnFrameworkInitializationCompleted();
    }
}
