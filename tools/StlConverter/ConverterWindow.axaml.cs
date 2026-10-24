using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace StlConverter;

/// <summary>
/// The desktop window (Windows, Linux, macOS) around the converter's view (<see cref="ConverterView"/>), on its own or
/// opened by PushPullMe for a mesh: file paths, dialog windows, and closing (with the result handed over, embedded).
/// </summary>
public partial class ConverterWindow : Window, IConverterHost
{
    private readonly ConverterView _view;

    public ConverterWindow() : this(null, embedded: false) { }

    /// <summary>
    /// Opened by another program (PushPullMe) with a mesh file: with a button that hands the result over
    /// (<see cref="Result"/>, the window closing with true) instead of saving it.
    /// </summary>
    public ConverterWindow(string path) : this(path, embedded: true) { }

    /// <summary>The window, with a file to open (or none); <paramref name="embedded"/>: opened by another program.</summary>
    public ConverterWindow(string? path, bool embedded)
    {
        InitializeComponent();
        Content = _view = new ConverterView(this, embedded);
        Opened += (_, _) => _view.Start(path);
    }

    /// <summary>What was handed over (embedded only), once <c>Overnemen</c> is clicked.</summary>
    public ConverterResult? Result => _view.Result;

    public async Task<string?> AskOpenPath(FilePickerFileType[] types)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Lang.T("Mesh openen", "Open a mesh"), AllowMultiple = false, FileTypeFilter = types,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> AskSavePath(string title, string name, string? folder, FilePickerFileType[] types)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = name, DefaultExtension = types[0].Patterns![0][2..], ShowOverwritePrompt = true, FileTypeChoices = types,
            SuggestedStartLocation = folder != null ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        return file?.TryGetLocalPath();
    }

    public Task Saved(string path) => Task.CompletedTask;

    /// <summary>A message with an OK button (as WPF's MessageBox), in a dialog window over this one.</summary>
    public async Task ShowMessage(string title, string text)
    {
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (IBrush)_view.FindResource("Page")!,
        };
        dlg.Content = ConverterView.Message(text, dlg.Close);
        dlg.Styles.Add(new StyleInclude(new Uri("avares://StlConverter.View/")) { Source = new Uri("avares://StlConverter.View/Theme.axaml") });
        await dlg.ShowDialog(this);
    }

    public bool HasCloseButton => false;

    public void Done(bool handedOver) => Close(handedOver);
}
