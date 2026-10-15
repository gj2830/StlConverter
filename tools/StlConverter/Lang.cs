using System.Globalization;
using System.IO;

namespace StlConverter;

/// <summary>
/// The language of what the converter says: Dutch or English, every text written in both side by side (<see cref="T"/>).
/// On its own it starts as chosen before (or as Windows speaks); opened from PushPullMe, that app sets it.
/// </summary>
public static class Lang
{
    /// <summary>English (otherwise Dutch).</summary>
    public static bool English { get; set; } = Load();

    /// <summary>The text in the language chosen.</summary>
    public static string T(string nl, string en) => English ? en : nl;

    private static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StlConverter", "taal.txt");

    private static bool Load()
    {
        if (Environment.GetEnvironmentVariable("PPM_LANG") is "en" or "nl") return Environment.GetEnvironmentVariable("PPM_LANG") == "en";
        try { if (File.Exists(File_)) return File.ReadAllText(File_).Trim() == "en"; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "nl";
    }

    /// <summary>The language switched, and kept for next time.</summary>
    public static void Switch()
    {
        English = !English;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, English ? "en" : "nl");
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
