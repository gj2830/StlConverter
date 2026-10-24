using Avalonia;
using Avalonia.Media;

namespace BrowserKit;

public static class BrowserFonts
{
    /// <summary>The fonts for a browser app: Inter, and where Inter has no such sign (the symbols of the tool buttons, the
    /// emoji in the texts) the Noto fonts; each of those covers some the others miss.</summary>
    public static AppBuilder WithBrowserFonts(this AppBuilder builder) => builder
        .WithInterFont()
        .With(new FontManagerOptions
        {
            FontFallbacks =
            [
                .. new[] { "Noto Sans Math", "Noto Sans Symbols 2", "Noto Sans Symbols", "Noto Emoji" }
                    .Select(name => new FontFallback { FontFamily = new FontFamily("avares://BrowserKit/Fonts#" + name) }),
            ],
        });
}
