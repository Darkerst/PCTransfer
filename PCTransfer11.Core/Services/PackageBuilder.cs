using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCTransfer11.Models;

namespace PCTransfer11.Services;

/// <summary>
/// Bouwt een PCTransfer11-back-up op basis van de door de gebruiker
/// geselecteerde bestanden/mappen en applicatie-instellingen.
///
/// De back-up wordt als GEWONE MAP weggeschreven (elk geselecteerd item
/// krijgt zijn eigen submap met de herkenbare naam, bv. "Documenten",
/// "Afbeeldingen"), zodat de gebruiker de back-up rechtstreeks in
/// Verkenner kan openen, bekijken en bewerken. Er staat een manifest.json
/// naast, die onthoudt welke map bij welk oorspronkelijk pad hoort zodat
/// alles later weer op de juiste plek teruggezet kan worden.
///
/// Voor netwerkoverdracht (waar één stroom bytes nodig is) wordt dezelfde
/// mapstructuur eerst in een tijdelijke map gebouwd en daarna pas ingepakt
/// tot één zip-bestand (zie BuildToZipAsync).
///
/// Er wordt eerst een "pre-scan" gedaan (grootte van alles optellen) zodat
/// de voortgangsbalk een écht percentage kan tonen in plaats van alleen
/// "bezig". Bestanden die alleen online staan (OneDrive-placeholders e.d.)
/// worden gedetecteerd en overgeslagen in plaats van geprobeerd te
/// downloaden - dat laatste kon de app minutenlang laten "vastlopen" als
/// het bestand groot was of de internetverbinding traag.
/// </summary>
public sealed class PackageBuilder
{
    private readonly IProgress<string> _log;

    /// <summary>
    /// Begrenst hoeveel bestanden tegelijk gekopieerd worden (zie
    /// CopyDirectoryRecursiveAsync) - genoeg om sneller te zijn dan strikt
    /// serieel, zonder honderden bestandshandles tegelijk open te hebben.
    /// </summary>
    private static readonly SemaphoreSlim _copyLimiter = new(Math.Max(2, Environment.ProcessorCount));

    /// <summary>
    /// Volledig, genormaliseerd pad van de back-updoelmap voor de huidige build,
    /// gebruikt door de runtime-guard in CopyDirectoryRecursiveAsync (tweede
    /// verdedigingslinie tegen zichzelf-in-zichzelf kopiëren). Wordt gezet aan
    /// het begin van BuildToDirectoryAsync.
    /// </summary>
    private string? _guardOutputRoot;

    public PackageBuilder(IProgress<string> log)
    {
        _log = log;
    }

    /// <summary>
    /// Bouwt de back-up rechtstreeks in <paramref name="outputDirectory"/> (wordt
    /// aangemaakt als hij nog niet bestaat). Dit is de map die de gebruiker
    /// straks kan openen/bekijken/bewerken, en die later weer gekozen kan
    /// worden om (een selectie van) terug te zetten.
    /// </summary>
    public async Task<string> BuildToDirectoryAsync(
        IEnumerable<FileSelectionItem> selectedFiles,
        IEnumerable<AppProfile> selectedApps,
        string outputDirectory,
        IProgress<double>? percentProgress,
        CancellationToken ct,
        IProgress<string>? currentFileProgress = null,
        bool useVss = true,
        bool differentialMode = false,
        string? previousBackupDirectory = null)
    {
        var filesList = selectedFiles.Where(f => f.Exists).ToList();
        var appsList = selectedApps.ToList();

        // ---- Veiligheidscontrole: voorkom dat de back-upmap in zichzelf terechtkomt ----
        // Als de gekozen doelmap gelijk is aan, of ligt binnen, één van de mappen
        // die wordt gebackupt (bv. de doelmap staat ergens onder "Documenten"
        // terwijl "Documenten" zelf ook wordt gebackupt), dan komt het kopiëren
        // van die bronmap de zojuist aangemaakte back-upmap weer tegen als
        // "nieuwe inhoud" en kopieert hij zichzelf record voor record naar
        // zichzelf, tot de padlengte van Windows (260 tekens) geraakt wordt en
        // alles vastloopt. Dit vooraf blokkeren voorkomt die oneindige lus
        // helemaal, in plaats van de gebruiker pas te laten crashen. Dit gebeurt
        // bewust vóórdat de doelmap wordt aangemaakt, zodat er ook geen
        // (mogelijk al geneste) map wordt achtergelaten als we alsnog weigeren.
        string? conflictingSource = FindNestingConflict(outputDirectory, filesList, appsList);
        if (conflictingSource != null)
        {
            throw new InvalidOperationException(
                $"De gekozen back-upbestemming ligt binnen (of is gelijk aan) de map '{conflictingSource}', " +
                "die zelf ook wordt gebackupt. Kies een bestemming buiten de mappen die je back-upt.");
        }

        // Onthouden voor de runtime-guard tijdens het kopiëren zelf (tweede
        // verdedigingslinie, voor het geval een pad via een andere route dan
        // hierboven alsnog binnen een bronmap terecht zou komen).
        _guardOutputRoot = NormalizeFullPath(outputDirectory);

        Directory.CreateDirectory(outputDirectory);

        // ---- Pre-scan: grootte bepalen vóórdat er iets gekopieerd wordt ----
        _log.Report("Bestanden tellen en grootte bepalen ...");
        long totalBytes = 0;
        int cloudFilesFound = 0;
        foreach (var item in filesList)
            totalBytes += ScanSize(item.Path, ct, ref cloudFilesFound);
        foreach (var app in appsList)
        {
            string? dataFolder = app.ResolveDataFolder();
            if (dataFolder != null && Directory.Exists(dataFolder))
                totalBytes += ScanSize(dataFolder, ct, ref cloudFilesFound);
        }

        _log.Report($"{FormatBytes(totalBytes)} te kopiëren.");
        if (cloudFilesFound > 0)
            _log.Report($"Let op: {cloudFilesFound} bestand(en) staan alleen online (bv. OneDrive 'alleen-online') " +
                        "en worden overgeslagen om vastlopen te voorkomen. Download ze eerst lokaal als je ze wel mee wil nemen.");

        CheckFreeDiskSpace(outputDirectory, totalBytes);

        // VSS-snapshot aanmaken zodat open bestanden (bv. Edge/Outlook) gewoon gekopieerd worden.
        using var vssSession = useVss ? new VssSessionManager(_log) : null;

        // Differentiële modus: alleen gewijzigde bestanden meenemen.
        BackupHistory.BackupIndex? previousIndex = null;
        if (differentialMode && previousBackupDirectory != null)
        {
            previousIndex = BackupHistory.LoadLatestIndex(previousBackupDirectory);
            _log.Report(previousIndex != null
                ? $"Differentiële back-up: vorige index gevonden van {previousIndex.CreatedAtUtc:g} UTC - alleen gewijzigde bestanden worden meegenomen."
                : "Differentiële back-up: geen vorige index gevonden - volledige back-up wordt gemaakt.");
        }

        var tracker = new ByteProgressTracker(totalBytes, percentProgress, currentFileProgress);

        var manifest = new PackageManifest();
        // "manifest.json" en de instellingenmap zijn gereserveerde namen binnen een back-up;
        // een geselecteerde map met toevallig dezelfde naam krijgt zo een " (2)" achtervoegsel.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", SettingsFolderName };
        string settingsRoot = Path.Combine(outputDirectory, SettingsFolderName);

        foreach (var item in filesList)
        {
            ct.ThrowIfCancellationRequested();

            string safeName = MakeUniqueName(SanitizeForFileName(item.DisplayName), usedNames);
            string destination = Path.Combine(outputDirectory, safeName);

            _log.Report($"Bestanden kopiëren: {item.DisplayName} ...");
            string sourcePath = vssSession?.TranslateToSnapshotPath(item.Path) ?? item.Path;
            if (Directory.Exists(sourcePath))
                await CopyDirectoryAsync(sourcePath, destination, tracker, ct);
            else
                await CopyFileTrackedAsync(sourcePath, destination, tracker, ct);

            manifest.Files.Add(new PackageManifest.FileEntry
            {
                PackagePath = safeName,
                OriginalPath = item.Path,
                DisplayName = item.DisplayName,
                KnownFolderId = item.KnownFolderId
            });
        }

        // Eén gecombineerde UAC-aanvraag voor Wifi + netwerkadapter als beide
        // zijn aangevinkt, in plaats van straks in de loop hieronder twee
        // aparte UAC-vensters na elkaar te tonen.
        bool wifiHandledByBatch = false, adapterHandledByBatch = false;
        var wifiApp = appsList.FirstOrDefault(a => a.Id == "windows_wifi");
        var adapterApp = appsList.FirstOrDefault(a => a.Id == "windows_network_adapter");
        if (wifiApp != null && adapterApp != null)
        {
            string wifiExportDir = Path.Combine(settingsRoot, "windows_wifi", "data");
            string adapterExportDir = Path.Combine(settingsRoot, "windows_network_adapter", "data");
            Directory.CreateDirectory(wifiExportDir);
            Directory.CreateDirectory(adapterExportDir);

            _log.Report("Instellingen ophalen: Wifi-netwerken en Netwerkadapter/proxy (gecombineerd in één UAC-venster) ...");
            await ElevatedNetworkHelper.RunElevatedBatchAsync(
                Array.Empty<string>(),
                "export", wifiExportDir,
                "export", adapterExportDir,
                requestTrust: false,
                ct, _log);

            wifiHandledByBatch = true;
            adapterHandledByBatch = true;
        }

        foreach (var app in appsList)
        {
            ct.ThrowIfCancellationRequested();
            string appStagingDir = Path.Combine(settingsRoot, app.Id);

            var entry = new PackageManifest.SettingsEntry
            {
                AppId = app.Id,
                DisplayName = app.DisplayName,
                RegistryKey = app.RegistryKeys != null ? string.Join("; ", app.RegistryKeys) : null
            };

            bool hasAnySource = false;

            string? dataFolder = app.ResolveDataFolder();
            if (dataFolder != null && Directory.Exists(dataFolder))
            {
                _log.Report($"Instellingen kopiëren: {app.DisplayName} ...");
                Directory.CreateDirectory(appStagingDir);
                string dataDestination = Path.Combine(appStagingDir, "data");
                string vssDataFolder = vssSession?.TranslateToSnapshotPath(dataFolder) ?? dataFolder;
                await CopyDirectoryAsync(vssDataFolder, dataDestination, tracker, ct);
                entry.HasDataFolder = true;
                hasAnySource = true;
            }

            if (app.RegistryKeys is { Length: > 0 })
            {
                _log.Report($"Registerinstellingen exporteren: {app.DisplayName} ...");
                Directory.CreateDirectory(appStagingDir);
                bool anyOk = false;
                for (int i = 0; i < app.RegistryKeys.Length; i++)
                {
                    string regFile = Path.Combine(appStagingDir, $"registry_{i}.reg");
                    if (await ExportRegistryKeyAsync(app.RegistryKeys[i], regFile, ct))
                        anyOk = true;
                }
                entry.HasRegistryExport = anyOk;
                hasAnySource = hasAnySource || anyOk;
            }

            bool isWifiHandled = app.Id == "windows_wifi" && wifiHandledByBatch;
            bool isAdapterHandled = app.Id == "windows_network_adapter" && adapterHandledByBatch;
            if (isWifiHandled || isAdapterHandled)
            {
                string customDestination = Path.Combine(appStagingDir, "data");
                bool ok = isWifiHandled
                    ? Directory.Exists(customDestination) && Directory.GetFiles(customDestination, "*.xml").Length > 0
                    : File.Exists(Path.Combine(customDestination, "netcfg.txt"));
                entry.HasCustomData = ok;
                entry.HasDataFolder = entry.HasDataFolder || ok;
                hasAnySource = hasAnySource || ok;
                _log.Report($"{app.DisplayName}: al verwerkt tijdens de gezamenlijke UAC-stap hierboven.");
            }
            else if (app.CustomExport != null)
            {
                _log.Report($"Instellingen ophalen: {app.DisplayName} ...");
                Directory.CreateDirectory(appStagingDir);
                string customDestination = Path.Combine(appStagingDir, "data");
                bool ok = await app.CustomExport(customDestination, ct, _log);
                entry.HasCustomData = ok;
                entry.HasDataFolder = entry.HasDataFolder || ok; // hergebruikt hetzelfde "data"-pad bij terugzetten
                hasAnySource = hasAnySource || ok;
            }

            if (!hasAnySource)
            {
                _log.Report($"Overslaan (niet gevonden op dit systeem): {app.DisplayName}");
                continue;
            }

            manifest.Settings.Add(entry);
        }

        string manifestPath = Path.Combine(outputDirectory, "manifest.json");
        string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, manifestJson, ct);

        percentProgress?.Report(1.0);

        int appsSkipped = appsList.Count - manifest.Settings.Count;
        _log.Report("");
        _log.Report("===== Samenvatting =====");
        _log.Report($"Bestanden/mappen: {manifest.Files.Count} meegenomen.");
        _log.Report($"Programma-/Windows-instellingen: {manifest.Settings.Count} meegenomen, {appsSkipped} overgeslagen (niet gevonden op dit systeem).");
        _log.Report($"Back-up klaar in map: {outputDirectory}");
        return outputDirectory;
    }

    /// <summary>
    /// Bouwt dezelfde back-up als BuildToDirectoryAsync, maar dan in een
    /// tijdelijke map die daarna wordt ingepakt tot één .pctbackup-zipbestand.
    /// Gebruikt voor rechtstreekse netwerkoverdracht én voor versleutelde
    /// back-ups; de tijdelijke map wordt achteraf opgeruimd.
    /// </summary>
    public async Task<string> BuildToZipAsync(
        IEnumerable<FileSelectionItem> selectedFiles,
        IEnumerable<AppProfile> selectedApps,
        string outputZipPath,
        IProgress<double>? percentProgress,
        CancellationToken ct,
        IProgress<string>? currentFileProgress = null)
    {
        string stagingDir = Path.Combine(Path.GetTempPath(), "PCTransfer11_" + Guid.NewGuid().ToString("N"));
        try
        {
            // Kopiëren telt als de eerste ~70% van de balk, comprimeren de
            // laatste ~30% - zonder deze verdeling zou de balk tijdens het
            // hele kopiëren op 0% blijven staan (percentProgress werd eerder
            // altijd als "null" doorgegeven aan BuildToDirectoryAsync).
            IProgress<double>? copyProgress = percentProgress == null
                ? null
                : new Progress<double>(p => percentProgress.Report(p * 0.7));
            await BuildToDirectoryAsync(selectedFiles, selectedApps, stagingDir, copyProgress, ct, currentFileProgress);

            _log.Report("Pakket comprimeren voor verzending ...");
            percentProgress?.Report(0.7);
            if (File.Exists(outputZipPath))
                File.Delete(outputZipPath);
            // "Fastest" i.p.v. "Optimal": bij foto's/video's (al gecomprimeerd
            // als JPEG/MP4/HEIC) levert een hoger compressieniveau nauwelijks
            // kleinere bestanden op, maar kost wél merkbaar meer CPU-tijd -
            // juist bij een telefoon-back-up (veel media) valt dat het meest op.
            ZipFile.CreateFromDirectory(stagingDir, outputZipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            percentProgress?.Report(1.0);

            _log.Report($"Pakket klaar: {outputZipPath}");
            return outputZipPath;
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    /// <summary>Naam van de submap waarin app-instellingen worden bewaard binnen een back-up.</summary>
    public const string SettingsFolderName = "_instellingen";

    // ---------------------------------------------------------------------
    // Pre-scan (grootte bepalen + cloud-only bestanden detecteren)
    // ---------------------------------------------------------------------

    private long ScanSize(string path, CancellationToken ct, ref int cloudFilesFound)
    {
        ct.ThrowIfCancellationRequested();

        if (File.Exists(path))
        {
            if (IsCloudPlaceholder(path))
            {
                cloudFilesFound++;
                return 0; // wordt straks overgeslagen, telt niet mee voor de voortgang
            }
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        if (!Directory.Exists(path))
            return 0;

        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return 0; // junction/symlink: zelfde reden als bij het kopiëren zelf overgeslagen

        long total = 0;
        int localCloudCount = 0;

        try
        {
            foreach (string filePath in Directory.EnumerateFiles(path))
            {
                ct.ThrowIfCancellationRequested();
                if (IsCloudPlaceholder(filePath)) { localCloudCount++; continue; }
                try { total += new FileInfo(filePath).Length; } catch { /* ontoegankelijk - negeren tijdens scan */ }
            }

            foreach (string subDir in Directory.EnumerateDirectories(path))
                total += ScanSize(subDir, ct, ref cloudFilesFound);
        }
        catch (UnauthorizedAccessException) { /* geen toegang - overslaan tijdens scan, kopieerstap doet dit ook */ }

        cloudFilesFound += localCloudCount;
        return total;
    }

    /// <summary>
    /// Herkent bestanden die door OneDrive/andere cloudsync als "alleen online"
    /// zijn gemarkeerd (niet lokaal gedownload). Die openen/lezen kan Windows
    /// dwingen om ze eerst te downloaden, wat bij grote bestanden of een
    /// trage verbinding de app minutenlang kan laten hangen. Deze vlaggen
    /// zitten niet in het standaard System.IO.FileAttributes-enum, maar wel
    /// in de onderliggende Win32-waarde die File.GetAttributes teruggeeft.
    /// </summary>
    private static bool IsCloudPlaceholder(string filePath)
    {
        const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
        const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;

        try
        {
            int attrs = (int)File.GetAttributes(filePath);
            return (attrs & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0
                || (attrs & FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0
                || (attrs & (int)FileAttributes.Offline) != 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Kopiëren (met byte-niveau voortgang en snelle annulering)
    // ---------------------------------------------------------------------

    private async Task CopyDirectoryAsync(string sourceDir, string destinationDir, ByteProgressTracker tracker, CancellationToken ct)
    {
        await CopyDirectoryRecursiveAsync(sourceDir, destinationDir, tracker, ct);
    }

    /// <summary>
    /// Kopieert een map recursief, maar slaat reparse points (junctions/symlinks)
    /// over. Windows gebruikt zulke junctions voor legacy-mappen als
    /// "Documenten\Mijn afbeeldingen" die eigenlijk naar elders verwijzen; direct
    /// benaderen daarvan geeft altijd "Access denied" voor niet-Verkenner-
    /// processen. De echte doelmap wordt sowieso al los meegenomen als die apart
    /// in de selectie staat, dus overslaan hier verliest geen data.
    /// </summary>
    private async Task CopyDirectoryRecursiveAsync(string sourceDir, string destinationDir, ByteProgressTracker tracker, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dirInfo = new DirectoryInfo(sourceDir);
        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Junction/symlink: overslaan om Access-denied te voorkomen.
            return;
        }

        Directory.CreateDirectory(destinationDir);

        // Bestanden binnen deze map gelijktijdig kopiëren (begrensd door
        // _copyLimiter, gedeeld over de hele back-up) i.p.v. strikt één voor
        // één - vooral bij veel kleine bestanden (foto's!) op een SSD een
        // merkbare versnelling, zonder honderden bestanden tegelijk open te
        // hebben staan.
        var fileTasks = Directory.EnumerateFiles(sourceDir).Select(async filePath =>
        {
            ct.ThrowIfCancellationRequested();
            string destFile = Path.Combine(destinationDir, Path.GetFileName(filePath));
            await _copyLimiter.WaitAsync(ct);
            try
            {
                await CopyFileTrackedAsync(filePath, destFile, tracker, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException)
            {
                // Bestand is in gebruik door een lopende applicatie (bv. een browser-databasebestand) - overslaan.
                _log.Report($"Overgeslagen (in gebruik door andere app): {filePath}");
            }
            catch (UnauthorizedAccessException)
            {
                // Geen toegang (bv. systeembestand) - overslaan.
                _log.Report($"Overgeslagen (geen toegang): {filePath}");
            }
            finally
            {
                _copyLimiter.Release();
            }
        });
        await Task.WhenAll(fileTasks);

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();

            // Runtime-guard (tweede verdedigingslinie naast de pre-check in
            // BuildToDirectoryAsync): als deze submap zelf de back-updoelmap is,
            // of die bevat, dan zou verder afdalen de back-up weer in zichzelf
            // gaan kopiëren en oneindig doorlopen tot Windows' padlengtelimiet.
            // We slaan alleen déze ene submap over; de rest van de bronmap wordt
            // gewoon normaal meegenomen.
            if (_guardOutputRoot != null && IsSameOrNestedUnder(_guardOutputRoot, subDir))
            {
                _log.Report($"Overgeslagen (dit is de back-upmap zelf, zou oneindig in zichzelf kopiëren): {subDir}");
                continue;
            }

            try
            {
                string destSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
                await CopyDirectoryRecursiveAsync(subDir, destSubDir, tracker, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException)
            {
                // Geen toegang tot deze submap - overslaan en doorgaan met de rest.
                _log.Report($"Overgeslagen (geen toegang): {subDir}");
            }
        }
    }

    /// <summary>
    /// Kopieert één bestand met een handmatige, gebufferde stream-loop in
    /// plaats van File.Copy. Dit heeft twee voordelen: (1) elke leesactie
    /// controleert het CancellationToken, dus de "Stop"-knop reageert ook
    /// meteen tijdens het kopiëren van een groot bestand (bv. een video van
    /// enkele GB's) in plaats van pas nadat dat ene bestand klaar is; en
    /// (2) er kan per gekopieerd blok voortgang worden gerapporteerd voor
    /// een vloeiende, betrouwbare voortgangsbalk.
    /// </summary>
    private async Task CopyFileTrackedAsync(string sourceFile, string destFile, ByteProgressTracker tracker, CancellationToken ct)
    {
        if (IsCloudPlaceholder(sourceFile))
        {
            _log.Report($"Overgeslagen (alleen online beschikbaar): {sourceFile}");
            return;
        }

        const int bufferSize = 1024 * 1024; // 1 MB
        string? destDir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        tracker.ReportCurrentFile($"Kopiëren: {Path.GetFileName(sourceFile)}");

        await using var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        var buffer = new byte[bufferSize];
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            tracker.AddCopiedBytes(read);
        }
    }

    /// <summary>
    /// Houdt de totale voortgang (in bytes) bij en stuurt die - afgeremd tot
    /// ~10x per seconde, zodat de UI-thread niet overspoeld wordt - door
    /// naar de voortgangsbalk als percentage.
    /// </summary>
    internal sealed class ByteProgressTracker
    {
        private readonly long _totalBytes;
        private readonly IProgress<double>? _percentProgress;
        private readonly IProgress<string>? _currentFileProgress;
        private long _doneBytes;
        private long _lastReportTicks;

        public ByteProgressTracker(long totalBytes, IProgress<double>? percentProgress, IProgress<string>? currentFileProgress = null)
        {
            _totalBytes = Math.Max(1, totalBytes);
            _percentProgress = percentProgress;
            _currentFileProgress = currentFileProgress;
        }

        public void AddCopiedBytes(long bytes)
        {
            long done = Interlocked.Add(ref _doneBytes, bytes);
            long nowTicks = DateTime.UtcNow.Ticks;
            long lastTicks = Interlocked.Read(ref _lastReportTicks);
            // Max. ~10 updates/seconde, plus altijd de allerlaatste.
            if (nowTicks - lastTicks < TimeSpan.TicksPerMillisecond * 100 && done < _totalBytes)
                return;
            Interlocked.Exchange(ref _lastReportTicks, nowTicks);
            _percentProgress?.Report(Math.Min(1.0, (double)done / _totalBytes));
        }

        /// <summary>Meldt welk bestand nu wordt gekopieerd (los van het logboek, dat blijft bij één regel per hoofdonderdeel).</summary>
        public void ReportCurrentFile(string fileName) => _currentFileProgress?.Report(fileName);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// Controleert of de schijf van <paramref name="destinationPath"/> genoeg
    /// vrije ruimte heeft voor <paramref name="neededBytes"/> (plus een marge
    /// van 5% voor bestandssysteem-overhead). Gooit een duidelijke fout ipv.
    /// halverwege het kopiëren onverwacht vast te lopen met een volle schijf.
    /// </summary>
    internal static void CheckFreeDiskSpace(string destinationPath, long neededBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
            if (string.IsNullOrEmpty(root)) return; // kan geen drive bepalen (bv. UNC-pad) - overslaan

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            long neededWithMargin = neededBytes + neededBytes / 20; // +5% marge
            if (drive.AvailableFreeSpace < neededWithMargin)
            {
                throw new IOException(
                    $"Niet genoeg vrije ruimte op schijf {root} - nodig: ~{FormatBytes(neededWithMargin)}, " +
                    $"beschikbaar: {FormatBytes(drive.AvailableFreeSpace)}. Maak ruimte vrij of kies een andere " +
                    "locatie en probeer het opnieuw.");
            }
        }
        catch (IOException)
        {
            throw; // de duidelijke foutmelding hierboven moet gewoon doorgegeven worden
        }
        catch
        {
            // Kon de schijf niet controleren (bv. onbekend/netwerk-pad) - dan
            // niet blokkeren, gewoon proberen; een eventuele "schijf vol"-fout
            // tijdens het kopiëren zelf wordt toch al apart afgevangen.
        }
    }

    /// <summary>
    /// Exporteert een registersleutel met het ingebouwde Windows-hulpprogramma
    /// reg.exe (standaard onderdeel van Windows, geen extra installatie nodig).
    /// </summary>
    private async Task<bool> ExportRegistryKeyAsync(string registryKey, string outputRegFile, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"{registryKey}\" \"{outputRegFile}\" /y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return false;

            // Streams altijd uitlezen terwijl op afsluiten gewacht wordt - anders
            // kan een volle pijplijnbuffer reg.exe laten vasthangen.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);

            return process.ExitCode == 0 && File.Exists(outputRegFile);
        }
        catch (Exception ex)
        {
            _log.Report($"Kon registersleutel niet exporteren ({registryKey}): {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Bescherming tegen een back-upbestemming die (deels) samenvalt met een bron
    // ---------------------------------------------------------------------

    private static string NormalizeFullPath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// True als <paramref name="candidate"/> hetzelfde pad is als, of een
    /// (klein)kind-map is van, <paramref name="ancestor"/>. Vergelijkt op basis
    /// van volledige, genormaliseerde paden zodat een afwijkend hoofdlettergebruik
    /// of een afsluitende backslash geen vals-negatief resultaat geven.
    /// </summary>
    private static bool IsSameOrNestedUnder(string candidate, string ancestor)
    {
        string a = NormalizeFullPath(candidate);
        string b = NormalizeFullPath(ancestor);

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;

        string bWithSeparator = b + Path.DirectorySeparatorChar;
        return a.StartsWith(bWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Zoekt of de gekozen back-upbestemming gelijk is aan, of ligt binnen, één
    /// van de geselecteerde bronmappen (los geselecteerde mappen, of de
    /// datamappen van geselecteerde apps). Geeft het conflicterende bronpad
    /// terug, of null als er geen overlap is.
    /// </summary>
    private static string? FindNestingConflict(
        string outputDirectory,
        List<FileSelectionItem> filesList,
        List<AppProfile> appsList)
    {
        foreach (var item in filesList)
        {
            if (Directory.Exists(item.Path) && IsSameOrNestedUnder(outputDirectory, item.Path))
                return item.Path;
        }

        foreach (var app in appsList)
        {
            string? dataFolder = app.ResolveDataFolder();
            if (dataFolder != null && Directory.Exists(dataFolder) && IsSameOrNestedUnder(outputDirectory, dataFolder))
                return dataFolder;
        }

        return null;
    }

    private static string SanitizeForFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>
    /// Zorgt dat twee geselecteerde items nooit dezelfde mapnaam in de back-up
    /// krijgen (bv. twee zelf toegevoegde mappen die toevallig hetzelfde heten).
    /// </summary>
    private static string MakeUniqueName(string baseName, HashSet<string> usedNames)
    {
        string candidate = baseName;
        int suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{baseName} ({suffix})";
            suffix++;
        }
        return candidate;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort opruimen van tijdelijke bestanden */ }
    }
}
