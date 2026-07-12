using System;
using System.IO;

namespace PCTransfer11.Models;

/// <summary>
/// Eén door de gebruiker aan te vinken bestands- of map-item, met een
/// vriendelijke naam voor in de lijst.
/// </summary>
public sealed class FileSelectionItem
{
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsChecked { get; set; }
    public bool Exists => Directory.Exists(Path) || File.Exists(Path);

    /// <summary>
    /// Identificeert dit item als een bekende Windows-map (bv. "MyDocuments"
    /// of "Downloads"), zodat PCTransfer11 het pad bij het terugzetten
    /// OPNIEUW kan opzoeken op de machine waar wordt teruggezet, in plaats
    /// van het letterlijke pad van de bronmachine te hergebruiken. Dat
    /// laatste zou namelijk fout gaan zodra de gebruikersnaam op de nieuwe
    /// pc anders is dan op de oude (bv. "Microsoft" i.p.v. de oorspronkelijke
    /// gebruikersnaam) - de map moet altijd naar HET HUIDIGE profiel wijzen,
    /// ongeacht hoe dat heet. Blijft null voor een door de gebruiker zelf
    /// toegevoegde, aangepaste map (die heeft immers geen "bekende" naam).
    /// </summary>
    public string? KnownFolderId { get; set; }

    /// <summary>
    /// Optionele uitsluitingspatronen per map (bv. "node_modules", "*.tmp", ".git").
    /// Wildcards: * = elk teken, ? = één teken.
    /// </summary>
    public string[]? ExcludePatterns { get; set; }

    public bool IsExcluded(string fileOrFolderName)
    {
        if (ExcludePatterns == null || ExcludePatterns.Length == 0) return false;
        string name = System.IO.Path.GetFileName(fileOrFolderName);
        foreach (string pattern in ExcludePatterns)
            if (GlobMatch(name, pattern, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool GlobMatch(string str, string pattern, StringComparison cmp)
    {
        if (pattern == "*") return true;
        int s = 0, p = 0, starS = -1, starP = -1;
        while (s < str.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || string.Compare(str, s, pattern, p, 1, cmp) == 0))
            { s++; p++; }
            else if (p < pattern.Length && pattern[p] == '*')
            { starP = p++; starS = s; }
            else if (starP >= 0)
            { p = starP + 1; s = ++starS; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public static FileSelectionItem ForSpecialFolder(string displayName, Environment.SpecialFolder folder)
    {
        return new FileSelectionItem
        {
            DisplayName = displayName,
            Path = Environment.GetFolderPath(folder),
            KnownFolderId = folder.ToString()
        };
    }

    /// <summary>"Downloads" heeft geen eigen Environment.SpecialFolder-waarde, vandaar een eigen ID.</summary>
    public static FileSelectionItem ForDownloads(string displayName)
    {
        return new FileSelectionItem
        {
            DisplayName = displayName,
            Path = ResolveDownloadsPath(),
            KnownFolderId = "Downloads"
        };
    }

    /// <summary>
    /// Zoekt op DEZE machine het pad voor een bekende-map-ID op. Wordt zowel
    /// gebruikt bij het opbouwen van de lijst als - cruciaal - bij het
    /// terugzetten, zodat een andere gebruikersnaam op de doelmachine geen
    /// probleem is. Geeft null terug voor een onbekend/leeg ID (dan valt de
    /// aanroeper terug op het letterlijk opgeslagen pad).
    ///
    /// BELANGRIJK: een back-up kan ook van de Android-versie van PCTransfer11
    /// komen (zelfde manifest.json-formaat, voor cross-platform overdracht).
    /// Android gebruikt z'n eigen MediaStore-namen als KnownFolderId
    /// ("Photos", "Movies", "Music") in plaats van de Windows
    /// Environment.SpecialFolder-namen - vandaar de aliassen hieronder, anders
    /// zouden foto's/video's/muziek van een telefoon nergens op Windows
    /// terechtkomen (ze vielen terug op het letterlijke, ongeldige
    /// Android-pad/content://-URI van het brontoestel).
    /// </summary>
    public static string? ResolveKnownFolder(string? knownFolderId)
    {
        if (string.IsNullOrEmpty(knownFolderId)) return null;
        if (knownFolderId == "Downloads") return ResolveDownloadsPath();

        // Aliassen vanuit de Android-versie (zie PCTransfer11-android/KnownItems.kt)
        Environment.SpecialFolder? androidAlias = knownFolderId switch
        {
            "Photos" => Environment.SpecialFolder.MyPictures,
            "Movies" => Environment.SpecialFolder.MyVideos,
            "Music" => Environment.SpecialFolder.MyMusic,
            _ => null
        };
        if (androidAlias != null) return Environment.GetFolderPath(androidAlias.Value);

        return Enum.TryParse<Environment.SpecialFolder>(knownFolderId, out var folder)
            ? Environment.GetFolderPath(folder)
            : null;
    }

    private static string ResolveDownloadsPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
