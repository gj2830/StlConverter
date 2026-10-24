using System.Diagnostics;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using BrowserKit;

namespace StlConverter;

internal static class Program
{
    private static Task Main(string[] args)
    {
        // (Avalonia's warnings and errors, a GL program that does not compile for instance, in the browser's console)
        Trace.Listeners.Add(new ConsoleTraceListener());
        App.Begin = lifetime =>
        {
            if (lifetime is ISingleViewApplicationLifetime page) page.MainView = PageHost.CreateView(args.FirstOrDefault());
        };
        return AppBuilder.Configure<App>()
            .WithBrowserFonts()
            .LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
            .StartBrowserAppAsync("out");
    }
}

/// <summary>The page around the converter's view: files through the browser (<see cref="BrowserFiles"/>), messages
/// over the view, nothing to close.</summary>
internal sealed class PageHost : IConverterHost
{
    private ConverterView _view = null!;
    private BrowserFiles _files = null!;

    /// <summary>The view with this page around it; shown, it opens the mesh the page's address names (?open=…), if any.</summary>
    public static ConverterView CreateView(string? pageAddress)
    {
        var host = new PageHost();
        var view = host._view = new ConverterView(host, embedded: false);
        host._files = new BrowserFiles(view);
        bool started = false;
        view.AttachedToVisualTree += async (_, _) =>
        {
            if (started) return;
            started = true;
            view.Start(pageAddress != null ? await BrowserFiles.FromPageAddress(pageAddress) : null);
        };
        return view;
    }

    public Task<string?> AskOpenPath(FilePickerFileType[] types) => _files.Open(Lang.T("Mesh openen", "Open a mesh"), types);
    public Task<string?> AskSavePath(string title, string name, string? folder, FilePickerFileType[] types) => _files.AskSave(title, name, types);
    public Task Saved(string path) => _files.Saved(path);
    public Task ShowMessage(string title, string text) => _view.ShowMessageOver(title, text);
    public bool HasCloseButton => false;
    public void Done(bool handedOver) { }
}
