using Avalonia.Platform.Storage;

namespace StlConverter;

/// <summary>
/// What differs per system around the converter's view (<see cref="ConverterView"/>): the desktop has file paths and
/// dialog windows; the browser has neither (files come and go through the page, messages are shown over the view), and
/// inside PushPullMe the view is a layer that goes away when done. The view asks this for them.
/// </summary>
public interface IConverterHost
{
    /// <summary>A mesh to open, chosen by the user, as a path the converter can read (null: none).</summary>
    Task<string?> AskOpenPath(FilePickerFileType[] types);

    /// <summary>Where to save, chosen by the user, as a path to write to (null: nowhere); the first type is the one
    /// suggested.</summary>
    Task<string?> AskSavePath(string title, string name, string? folder, FilePickerFileType[] types);

    /// <summary>Written at <paramref name="path"/> (the one <see cref="AskSavePath"/> gave): for a system where that is
    /// not yet where the user chose (the browser hands it to the page).</summary>
    Task Saved(string path);

    /// <summary>A message with an OK button.</summary>
    Task ShowMessage(string title, string text);

    /// <summary>Whether the view shows a ✕ of its own (over PushPullMe in a browser: there is no window to close).</summary>
    bool HasCloseButton { get; }

    /// <summary>The converter is done: closed, or (opened by PushPullMe) with the result handed over
    /// (<see cref="ConverterView.Result"/>).</summary>
    void Done(bool handedOver);
}
