using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCTransfer11.Models;

namespace PCTransfer11.Services;

/// <summary>
/// Bevat de vooraf gedefinieerde lijst van applicaties waarvan PCTransfer11
/// instellingen/gegevens kan meenemen. Makkelijk uit te breiden: voeg een
/// nieuw AppProfile toe aan de lijst in <see cref="GetAll"/>.
/// </summary>
public static class KnownApps
{
    public static List<AppProfile> GetAll()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return new List<AppProfile>
        {
            // ---------------- Browsers ----------------
            new AppProfile
            {
                Id = "chrome",
                DisplayName = "Google Chrome (bladwijzers, instellingen)",
                Category = "Browsers",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default");
                    return Directory.Exists(path) ? path : null;
                },
                RelatedProcessNames = new[] { "chrome" },
                Note = "Bladwijzers en voorkeuren. Opgeslagen wachtwoorden gaan NIET mee " +
                       "(die zijn versleuteld aan dit Windows-account gekoppeld)."
            },
            new AppProfile
            {
                Id = "edge",
                DisplayName = "Microsoft Edge (bladwijzers, instellingen)",
                Category = "Browsers",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default");
                    return Directory.Exists(path) ? path : null;
                },
                RelatedProcessNames = new[] { "msedge" },
                Note = "Bladwijzers en voorkeuren. Opgeslagen wachtwoorden gaan NIET mee."
            },
            new AppProfile
            {
                Id = "firefox",
                DisplayName = "Mozilla Firefox (bladwijzers, instellingen)",
                Category = "Browsers",
                ResolveDataFolder = () =>
                {
                    var profilesRoot = Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles");
                    if (!Directory.Exists(profilesRoot)) return null;
                    // Pak het eerste profiel dat op ".default-release" eindigt, anders het eerste dat er is.
                    var dirs = Directory.GetDirectories(profilesRoot);
                    var preferred = dirs.FirstOrDefault(d => d.EndsWith(".default-release", StringComparison.OrdinalIgnoreCase));
                    return preferred ?? dirs.FirstOrDefault();
                },
                RelatedProcessNames = new[] { "firefox" },
                Note = "Bladwijzers, geschiedenis en voorkeuren van het standaardprofiel."
            },
            new AppProfile
            {
                Id = "opera",
                DisplayName = "Opera (bladwijzers, instellingen)",
                Category = "Browsers",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "Opera Software", "Opera Stable");
                    return Directory.Exists(path) ? path : null;
                },
                RelatedProcessNames = new[] { "opera" },
                Note = "Bladwijzers en voorkeuren. Opgeslagen wachtwoorden gaan NIET mee."
            },
            new AppProfile
            {
                Id = "ie_edge_favorites",
                DisplayName = "Favorieten (Internet Explorer / Edge)",
                Category = "Browsers",
                ResolveDataFolder = () =>
                {
                    var path = Environment.GetFolderPath(Environment.SpecialFolder.Favorites);
                    return Directory.Exists(path) ? path : null;
                },
                Note = "Internet Explorer is verwijderd uit Windows 11, maar gebruikte dezelfde Favorieten-map " +
                       "als Edge nog altijd gebruikt - deze map wordt hier meegenomen."
            },

            // ---------------- Communicatie & e-mail ----------------
            new AppProfile
            {
                Id = "thunderbird",
                DisplayName = "Mozilla Thunderbird (e-mail, adresboek, instellingen)",
                Category = "Communicatie & e-mail",
                ResolveDataFolder = () =>
                {
                    var profilesRoot = Path.Combine(roamingAppData, "Thunderbird", "Profiles");
                    if (!Directory.Exists(profilesRoot)) return null;
                    var dirs = Directory.GetDirectories(profilesRoot);
                    var preferred = dirs.FirstOrDefault(d => d.EndsWith(".default-release", StringComparison.OrdinalIgnoreCase));
                    return preferred ?? dirs.FirstOrDefault();
                },
                RelatedProcessNames = new[] { "thunderbird" },
                Note = "Volledig profiel: e-mails, accounts, adresboek, filters en instellingen. Kan groot zijn " +
                       "als er veel lokaal opgeslagen e-mail is."
            },
            new AppProfile
            {
                Id = "outlook",
                DisplayName = "Outlook (.pst / .ost e-mailbestanden)",
                Category = "Communicatie & e-mail",
                ResolveDataFolder = () =>
                {
                    // Standaardlocatie voor .ost (cache) en eventuele .pst-bestanden.
                    var path = Path.Combine(localAppData, "Microsoft", "Outlook");
                    return Directory.Exists(path) ? path : null;
                },
                RelatedProcessNames = new[] { "OUTLOOK" },
                Note = "Let op: .ost-bestanden zijn alleen een lokale cache van een online postvak (Exchange/" +
                       "Microsoft 365) en worden na terugzetten toch opnieuw gedownload door Outlook - die nemen " +
                       "onnodig ruimte in maar zijn verder onschadelijk. Een .pst-bestand (bv. een lokaal " +
                       "archief) is wél echt portable en wordt hiermee meegenomen. Accountinstellingen zelf " +
                       "(wachtwoorden/tokens) gaan niet mee; die stel je op de nieuwe pc opnieuw in."
            },
            new AppProfile
            {
                Id = "skype",
                DisplayName = "Skype (chatgeschiedenis, instellingen)",
                Category = "Communicatie & e-mail",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "Microsoft", "Skype for Desktop");
                    return Directory.Exists(path) ? path : null;
                },
                RelatedProcessNames = new[] { "Skype" },
                Note = "Lokale chatgeschiedenis en instellingen van de huidige Skype-app. " +
                       "(MSN Messenger en AIM bestaan al jaren niet meer en hebben dus niets om over te zetten.)"
            },

            // ---------------- Multimedia & downloads ----------------
            new AppProfile
            {
                Id = "qbittorrent",
                DisplayName = "qBittorrent (instellingen, categorieën, torrent-status)",
                Category = "Multimedia & downloads",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "qBittorrent");
                    return Directory.Exists(path) ? path : null;
                },
                Note = "Programma-instellingen, categorieën en de status/voortgang van torrents (BT_backup). " +
                       "De gedownloade bestanden zelf gaan hier niet in mee - voeg die map desgewenst apart toe " +
                       "op tab 1 via '+ Aangepaste map toevoegen'."
            },
            new AppProfile
            {
                Id = "aimp",
                DisplayName = "AIMP (afspeellijsten, instellingen)",
                Category = "Multimedia & downloads",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "AIMP");
                    return Directory.Exists(path) ? path : null;
                },
                Note = "Afspeellijsten en programma-instellingen van de geïnstalleerde (niet-portable) versie van AIMP."
            },
            new AppProfile
            {
                Id = "itunes",
                DisplayName = "iTunes (bibliotheek)",
                Category = "Multimedia & downloads",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes");
                    return Directory.Exists(path) ? path : null;
                },
                Note = "De iTunes-bibliotheek (iTunes Library.itl) en, als 'iTunes Media' op de standaardlocatie " +
                       "staat, ook de muziek/video's zelf. Bij een aangepaste mediamap: voeg die apart toe op tab 1."
            },

            // ---------------- Ontwikkeltools ----------------
            new AppProfile
            {
                Id = "vscode",
                DisplayName = "Visual Studio Code (instellingen, sneltoetsen)",
                Category = "Ontwikkeltools",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "Code", "User");
                    return Directory.Exists(path) ? path : null;
                },
                Note = "settings.json, keybindings.json en snippets. Extensies zelf gaan niet mee."
            },
            new AppProfile
            {
                Id = "winterminal",
                DisplayName = "Windows Terminal (instellingen)",
                Category = "Ontwikkeltools",
                ResolveDataFolder = () =>
                {
                    var packagesRoot = Path.Combine(localAppData, "Packages");
                    if (!Directory.Exists(packagesRoot)) return null;
                    var dir = Directory.GetDirectories(packagesRoot, "Microsoft.WindowsTerminal_*").FirstOrDefault();
                    if (dir == null) return null;
                    var settingsDir = Path.Combine(dir, "LocalState");
                    return Directory.Exists(settingsDir) ? settingsDir : null;
                },
                Note = "settings.json met kleurenschema's, profielen en sneltoetsen."
            },

            // ---------------- Windows-instellingen (register, HKCU-only, geen adminrechten nodig) ----------------
            new AppProfile
            {
                Id = "windows_desktop",
                DisplayName = "Bureaubladachtergrond en kleuren",
                Category = "Windows-instellingen",
                ResolveDataFolder = () => null, // alleen registry, geen bestandsmap
                RegistryKeys = new[]
                {
                    @"HKEY_CURRENT_USER\Control Panel\Desktop",
                    @"HKEY_CURRENT_USER\Control Panel\Colors",
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes"
                },
                Note = "Achtergrondafbeelding, kleurinstellingen en het actieve thema (via het register)."
            },
            new AppProfile
            {
                Id = "windows_explorer",
                DisplayName = "Verkenner en taakbalk (weergave-instellingen)",
                Category = "Windows-instellingen",
                ResolveDataFolder = () => null,
                RegistryKeys = new[]
                {
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband"
                },
                Note = "Bestandsextensies tonen, verborgen bestanden, taakbalk-indeling en vergelijkbare " +
                       "Verkenner-voorkeuren."
            },
            new AppProfile
            {
                Id = "windows_mouse_keyboard",
                DisplayName = "Muis en toetsenbord (snelheid, aanwijzers)",
                Category = "Windows-instellingen",
                ResolveDataFolder = () => null,
                RegistryKeys = new[]
                {
                    @"HKEY_CURRENT_USER\Control Panel\Mouse",
                    @"HKEY_CURRENT_USER\Control Panel\Keyboard"
                },
                Note = "Aanwijzersnelheid, dubbelklik-tijd, aanwijzerschema's en toetsenbordherhaling."
            },
            new AppProfile
            {
                Id = "windows_regional",
                DisplayName = "Taal- en regio-instellingen",
                Category = "Windows-instellingen",
                ResolveDataFolder = () => null,
                RegistryKeys = new[] { @"HKEY_CURRENT_USER\Control Panel\International" },
                Note = "Datum-, tijd- en getalnotatie, en de standaard weergavetaal van dit gebruikersaccount."
            },
            new AppProfile
            {
                Id = "windows_sounds",
                DisplayName = "Geluidsschema (systeemgeluiden)",
                Category = "Windows-instellingen",
                ResolveDataFolder = () => null,
                RegistryKeys = new[] { @"HKEY_CURRENT_USER\AppEvents" },
                Note = "Het gekozen geluidsschema en eventuele aangepaste systeemgeluiden."
            },
            new AppProfile
            {
                Id = "windows_network_drives",
                DisplayName = "Gekoppelde netwerkschijven (verkende mappen)",
                Category = "Netwerk",
                ResolveDataFolder = () => null,
                RegistryKeys = new[] { @"HKEY_CURRENT_USER\Network" },
                Note = "Onthoudt welke netwerkschijfletters (bv. Z:) naar welke netwerkmap wijzen. " +
                       "Werkt alleen als die netwerkmap op de nieuwe pc ook bereikbaar is."
            },
            new AppProfile
            {
                Id = "windows_proxy",
                DisplayName = "Proxy-instelling (Instellingen > Netwerk en internet > Proxy)",
                Category = "Netwerk",
                ResolveDataFolder = () => null,
                RegistryKeys = new[] { @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings" },
                Note = "De proxy-instelling die de meeste browsers en apps gebruiken. Vereist geen adminrechten."
            },
            new AppProfile
            {
                Id = "windows_network_adapter",
                DisplayName = "Netwerkadapter (vast IP, DNS, gateway) en systeemproxy — vereist adminrechten (UAC)",
                Category = "Netwerk",
                ResolveDataFolder = () => null,
                CustomExport = (destDir, ct, log) => ElevatedNetworkHelper.RunElevatedExportAsync("adapter", destDir, ct, log),
                CustomImport = (sourceDir, ct, log) => ElevatedNetworkHelper.RunElevatedImportAsync("adapter", sourceDir, ct, log),
                RequiresElevation = true,
                Note = "Staat bewust NIET standaard aangevinkt. Vereist adminrechten: Windows toont zowel bij het " +
                       "maken als bij het terugzetten van de back-up een UAC-bevestigingsvenster. Bevat een vast " +
                       "IP-adres, DNS-servers en gateway per netwerkadapter, plus de systeembrede (WinHTTP-)proxy. " +
                       "Weiger je de UAC-prompt, dan wordt dit onderdeel overgeslagen."
            },
            new AppProfile
            {
                Id = "windows_wifi",
                DisplayName = "Wifi-netwerken (namen en wachtwoorden) — vereist adminrechten (UAC)",
                Category = "Netwerk",
                ResolveDataFolder = () => null,
                CustomExport = (destDir, ct, log) => ElevatedNetworkHelper.RunElevatedExportAsync("wifi", destDir, ct, log),
                CustomImport = (sourceDir, ct, log) => ElevatedNetworkHelper.RunElevatedImportAsync("wifi", sourceDir, ct, log),
                RequiresElevation = true,
                Note = "Staat bewust NIET standaard aangevinkt. Vereist adminrechten (UAC) om de wachtwoorden in " +
                       "klare tekst te kunnen meenemen. Weiger je de UAC-prompt, dan gaan alleen de netwerknaam en " +
                       "het beveiligingstype mee (zonder wachtwoord) - dat lukt namelijk wel zonder adminrechten."
            },

            // ---------------- Ontwikkeling & tools ----------------
            new AppProfile
            {
                Id = "vscode",
                DisplayName = "Visual Studio Code (instellingen, keybindings, snippets)",
                Category = "Ontwikkeling & tools",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(localAppData, "Code", "User");
                    return Directory.Exists(path) ? path : null;
                },
                RelatedProcessNames = new[] { "Code" },
                Note = "Instellingen, sneltoetsen en snippets. Extensies gaan niet mee maar worden opnieuw geïnstalleerd via Settings Sync."
            },
            new AppProfile
            {
                Id = "notepadplusplus",
                DisplayName = "Notepad++ (instellingen, sneltoetsen, stijlen)",
                Category = "Ontwikkeling & tools",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "Notepad++");
                    return Directory.Exists(path) ? path : null;
                },
                RelatedProcessNames = new[] { "notepad++" },
                Note = "config.xml, shortcuts.xml, stylers.xml en pluginConfig. Installeer Notepad++ eerst opnieuw."
            },
            new AppProfile
            {
                Id = "7zip",
                DisplayName = "7-Zip (instellingen)",
                Category = "Ontwikkeling & tools",
                ResolveDataFolder = () => null,
                RegistryKeys = new[] { @"HKEY_CURRENT_USER\Software\7-Zip" },
                Note = "7-Zip slaat instellingen op in het register."
            },
            new AppProfile
            {
                Id = "winrar",
                DisplayName = "WinRAR (instellingen, thema's)",
                Category = "Ontwikkeling & tools",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "WinRAR");
                    return Directory.Exists(path) ? path : null;
                },
                RegistryKeys = new[] { @"HKEY_CURRENT_USER\Software\WinRAR" },
                Note = "Instellingen en thema's. Licentie gaat NIET mee."
            },
            // ---------------- Games ----------------
            new AppProfile
            {
                Id = "steam",
                DisplayName = "Steam (game-saves, lokale gebruikersdata)",
                Category = "Games",
                ResolveDataFolder = () =>
                {
                    var roaming = Path.Combine(roamingAppData, "Steam");
                    if (Directory.Exists(roaming)) return roaming;
                    var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "userdata");
                    return Directory.Exists(pf) ? pf : null;
                },
                RelatedProcessNames = new[] { "steam", "steamwebhelper" },
                Note = "Lokale game-saves. Games zelf moeten opnieuw worden gedownload. Kan groot zijn."
            },
            // ---------------- Communicatie (extra) ----------------
            new AppProfile
            {
                Id = "discord",
                DisplayName = "Discord (instellingen, sneltoetsen)",
                Category = "Communicatie & e-mail",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "discord");
                    return Directory.Exists(path) ? path : null;
                },
                RelatedProcessNames = new[] { "Discord" },
                Note = "Lokale instellingen. Account/servers zijn in de cloud opgeslagen."
            },
            new AppProfile
            {
                Id = "whatsapp_win",
                DisplayName = "WhatsApp voor Windows (lokale cache)",
                Category = "Communicatie & e-mail",
                ResolveDataFolder = () =>
                {
                    var ms = Path.Combine(localAppData, "Packages", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm", "LocalState");
                    if (Directory.Exists(ms)) return ms;
                    var classic = Path.Combine(roamingAppData, "WhatsApp");
                    return Directory.Exists(classic) ? classic : null;
                },
                RelatedProcessNames = new[] { "WhatsApp" },
                Note = "Lokale cache van de Windows-app."
            },
            // ---------------- Multimedia ----------------
            new AppProfile
            {
                Id = "vlc",
                DisplayName = "VLC mediaspeler (instellingen, afspeellijsten)",
                Category = "Multimedia & downloads",
                ResolveDataFolder = () =>
                {
                    var path = Path.Combine(roamingAppData, "vlc");
                    return Directory.Exists(path) ? path : null;
                },
                Note = "VLC-instellingen (vlcrc) en afspeellijsten."
            },
        };
    }
}
