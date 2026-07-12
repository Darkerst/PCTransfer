using System;
using System.Threading;
using System.Threading.Tasks;

namespace PCTransfer11.Models;

/// <summary>
/// Beschrijft waar de instellingen/gegevens van een bekende applicatie
/// (of Windows-instelling) op schijf en/of in het register staan, zodat
/// PCTransfer11 ze kan meenemen in een pakket.
/// </summary>
public sealed class AppProfile
{
    /// <summary>Unieke, korte sleutel - wordt gebruikt als mapnaam in het pakket.</summary>
    public required string Id { get; init; }

    /// <summary>Naam zoals getoond in de UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Categorie waaronder dit item wordt getoond/gegroepeerd in de UI
    /// (bv. "Browsers", "Windows-instellingen", "Netwerk").
    /// </summary>
    public string Category { get; init; } = "Overig";

    /// <summary>Of dit item standaard aangevinkt staat.</summary>
    public bool IsChecked { get; set; }

    /// <summary>
    /// Levert het absolute pad naar de map met gegevens op, of null als de
    /// applicatie niet op dit systeem is geïnstalleerd / gevonden kan worden.
    /// </summary>
    public required Func<string?> ResolveDataFolder { get; init; }

    /// <summary>
    /// Optioneel: één of meerdere HKEY_CURRENT_USER-registersleutels (volledig
    /// pad, bv. "HKEY_CURRENT_USER\Control Panel\Desktop") die elk apart
    /// worden geëxporteerd/geïmporteerd via het ingebouwde reg.exe. Bewust
    /// alleen HKCU: dat vereist geen adminrechten en raakt nooit
    /// systeembrede/andere-gebruikers-instellingen.
    /// </summary>
    public string[]? RegistryKeys { get; init; }

    /// <summary>
    /// Optioneel: aangepaste exportlogica voor onderdelen die niet gewoon
    /// een bestaande map of registersleutel zijn (bv. Wifi-profielen via
    /// netsh). Krijgt de doelmap mee en levert true op bij succes.
    /// </summary>
    public Func<string, CancellationToken, IProgress<string>, Task<bool>>? CustomExport { get; init; }

    /// <summary>Bijbehorende aangepaste terugzet-logica voor <see cref="CustomExport"/>.</summary>
    public Func<string, CancellationToken, IProgress<string>, Task>? CustomImport { get; init; }

    /// <summary>
    /// Procesnaam(en) (zonder ".exe") die dit onderdeel op slot kunnen zetten
    /// terwijl ze draaien - bv. een browser die zijn eigen History/Cookies-
    /// bestand vergrendelt. Vóór een back-up wordt gecontroleerd of zo'n
    /// proces nog draait, zodat de gebruiker de kans krijgt het eerst af te
    /// sluiten in plaats van dat cruciale bestanden stilletjes "in gebruik
    /// door andere app" worden overgeslagen.
    /// </summary>
    public string[]? RelatedProcessNames { get; init; }

    /// <summary>
    /// True als dit onderdeel een UAC-adminrechten-prompt laat zien zodra het
    /// wordt meegenomen. Zulke items staan bewust NIET standaard aangevinkt
    /// (in tegenstelling tot de rest), zodat een gebruiker nooit onverwacht
    /// een UAC-venster krijgt zonder dat expliciet te hebben gekozen.
    /// </summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Korte toelichting, getoond als tooltip.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Of deze applicatie/instelling op dit systeem gevonden is en dus
    /// aangevinkt kan worden. Wordt door de UI gebruikt om niet-gevonden
    /// items uit te grijzen.
    /// </summary>
    public bool IsAvailable => ResolveDataFolder() != null
                               || (RegistryKeys?.Length ?? 0) > 0
                               || CustomExport != null;
}
