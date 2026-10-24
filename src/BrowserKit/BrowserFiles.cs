using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace BrowserKit;

/// <summary>
/// Files in a browser, which has no paths: a chosen file is copied into the runtime's own in-memory folder and read from
/// there as from a path; to save, the place is chosen first, the file is written in that folder, and then copied to the
/// place chosen (<see cref="Saved"/>).
/// </summary>
/// <param name="of">A control in the page (its storage is the page's).</param>
public sealed class BrowserFiles(Visual of)
{
    /// <summary>The runtime's in-memory folder the files pass through.</summary>
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "browserfiles");

    private IStorageFile? _saveTo;

    private IStorageProvider Storage => TopLevel.GetTopLevel(of)!.StorageProvider;

    /// <summary>A file chosen by the user, as a path to read (null: none).</summary>
    public async Task<string?> Open(string title, IReadOnlyList<FilePickerFileType> types)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = types });
        if (files.Count == 0) return null;
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, Path.GetFileName(files[0].Name));
        await using (var from = await files[0].OpenReadAsync())
        await using (var to = File.Create(path))
            await from.CopyToAsync(to);
        return path;
    }

    /// <summary>The file named in the page's address (<c>?open=name.stl</c>, next to the page or a full address),
    /// fetched into the in-memory folder; null when the address names none or it cannot be fetched. (To open a model by
    /// a link, and the test aid of the browser apps.)</summary>
    public static async Task<string?> FromPageAddress(string pageAddress)
    {
        if (!Uri.TryCreate(pageAddress, UriKind.Absolute, out var page)) return null;
        string? name = page.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == "open")
            .Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(page, name, out var file)) return null;
        try
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(file);
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, Path.GetFileName(file.LocalPath));
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Where to save, chosen by the user, as a path to write to (null: nowhere); then <see cref="Saved"/>.
    /// The first type is the one suggested; the extension the user chose decides the format, as on the desktop.</summary>
    public async Task<string?> AskSave(string title, string name, IReadOnlyList<FilePickerFileType> types)
    {
        string extension = types[0].Patterns![0][2..];
        _saveTo = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = name, DefaultExtension = extension, FileTypeChoices = types, ShowOverwritePrompt = true,
        });
        if (_saveTo == null) return null;
        Directory.CreateDirectory(Folder);
        string file = Path.GetFileName(_saveTo.Name);
        return Path.Combine(Folder, Path.HasExtension(file) ? file : file + "." + extension);
    }

    /// <summary>Written at the path <see cref="AskSave"/> gave: copied to the place the user chose.</summary>
    public async Task Saved(string path)
    {
        if (_saveTo is not { } target) return;
        _saveTo = null;
        await using var from = File.OpenRead(path);
        await using var to = await target.OpenWriteAsync();
        await from.CopyToAsync(to);
    }
}
