using System.Windows;
using System.Windows.Controls;

namespace StlConverter;

/// <summary>
/// The texts of the window as written in its XAML (Dutch) and in English: every tooltip, label and button text is
/// looked up here when the window opens and when the language is switched.
/// </summary>
internal static class XamlTexts
{


    /// <summary>The window's texts in the language chosen, all through it (from either language).</summary>
    public static void Apply(DependencyObject root) => Walk(root);

    private static string Tr(string s) => Texts.Tr(s);

    private static void Walk(object node)
    {
        if (node is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = Tr(tip);
        switch (node)
        {
            case TextBlock tb when tb.Text is { Length: > 0 }: tb.Text = Tr(tb.Text); break;
            case ContentControl cc when cc.Content is string text: cc.Content = Tr(text); break;
        }
        if (node is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
    }
}
