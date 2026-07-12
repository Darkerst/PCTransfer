namespace PCTransfer11.Models;

/// <summary>
/// Eén aan te vinken item uit een ingelezen back-upmap, gebruikt op het
/// "Terugzetten"-scherm zodat de gebruiker kan kiezen wat wel en wat niet
/// wordt teruggezet (bv. alleen "Afbeeldingen" of "Documenten").
/// </summary>
public sealed class RestoreSelectionItem
{
    public string DisplayName { get; set; } = "";
    public bool IsChecked { get; set; } = true;

    /// <summary>Of dit een applicatie-instelling is (true) of een bestand/map (false).</summary>
    public bool IsSetting { get; set; }

    /// <summary>
    /// Voor bestanden: PackageManifest.FileEntry.PackagePath.
    /// Voor instellingen: PackageManifest.SettingsEntry.AppId.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Of dit item op DIT platform automatisch teruggezet kan worden. False
    /// voor instellingen die van het andere platform komen (bv. Android-
    /// contacten in een back-up die op Windows wordt teruggezet) - het
    /// bijbehorende, geëxporteerde bestand staat wel gewoon in de back-up,
    /// maar er is op dit platform geen automatische terugzet-actie voor.
    /// </summary>
    public bool IsSupported { get; set; } = true;
}
