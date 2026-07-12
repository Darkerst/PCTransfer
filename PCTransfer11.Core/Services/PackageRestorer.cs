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
/// Zet een PCTransfer11-back-up (een map met een manifest.json erin, zoals
/// gemaakt door PackageBuilder) terug op deze pc. Kan alles terugzetten, of
/// alleen een door de gebruiker gekozen selectie (bv. alleen "Afbeeldingen").
/// </summary>
public sealed class PackageRestorer
{
    private readonly IProgress<string> _log;

    /// <summary>Begrenst hoeveel bestanden tegelijk teruggezet worden (zie CopyDirectoryAsync).</summary>
    private static readonly SemaphoreSlim _copyLimiter = new(Math.Max(2, Environment.ProcessorCount));

    public PackageRestorer(IProgress<string> log)
    {
        _log = log;
    }

    /// <summary>
    /// Leest het manifest.json in de opgegeven back-upmap in, zodat de UI kan
    /// tonen wat er in de back-up zit vóórdat er iets wordt teruggezet.
    /// </summary>
    public static PackageManifest LoadManifest(string backupFolderPath)
    {
        string manifestPath = Path.Combine(backupFolderPath, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Dit is geen geldige PCTransfer11-back-upmap (manifest.json ontbreekt).");

        string json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<PackageManifest>(json)
               ?? throw new InvalidOperationException("Het manifest kon niet worden gelezen.");
    }

    /// <summary>
    /// Pakt een .pctbackup-zipbestand volledig uit naar <paramref name="destDir"/>
    /// (behoudt de mapstructuur uit het pakket, bv. "Documenten/", "Afbeeldingen/",
    /// "_instellingen/"), met per-bestand voortgang. Anders dan
    /// <see cref="RestoreZipAsync"/> wordt hier NIET per onderdeel naar de
    /// bijbehorende Windows-map (Documenten/Afbeeldingen/...) verspreid - alles
    /// komt in dezelfde, ene doelmap terecht. Gebruikt wanneer je een ontvangen
    /// back-up (bv. van een telefoon) gewoon compleet in één overzichtelijke,
    /// zelf benoemde map wil hebben, in plaats van verspreid over je bestaande
    /// Documenten/Afbeeldingen/Video's.
    /// </summary>
    public static async Task ExtractZipWithProgressAsync(string zipPath, string destDir, IProgress<double>? percent,
        IProgress<string>? currentFileProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        string fullDestRoot = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);

        List<string> entryNames;
        long totalBytes;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entries = archive.Entries.Where(en => !string.IsNullOrEmpty(en.Name)).ToList();
            entryNames = entries.Select(en => en.FullName).ToList();
            totalBytes = entries.Sum(en => en.Length);
        }

        long done = 0;
        // Verdeel de bestanden over een vast aantal werktaken, elk met hun
        // EIGEN ZipArchive-handle (één instantie deelt geen gelijktijdige
        // entry-reads toe) - sneller dan strikt één voor één, vooral bij veel
        // kleine bestanden (foto's van een telefoon-back-up).
        int concurrency = Math.Max(2, Environment.ProcessorCount);
        var buckets = entryNames
            .Select((name, i) => (name, i))
            .GroupBy(x => x.i % concurrency)
            .Select(g => g.Select(x => x.name).ToList());

        var tasks = buckets.Select(async bucket =>
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (string entryName in bucket)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(entryName);
                if (entry == null) continue;

                string destPath = Path.GetFullPath(Path.Combine(destDir, entryName));
                if (!destPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                    continue; // veiligheid tegen een kwaadaardig/corrupt zip-pad dat buiten destDir zou schrijven

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                currentFileProgress?.Report($"Uitpakken: {entry.Name}");

                await using var entryStream = entry.Open();
                await using var outStream = File.Create(destPath);
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await entryStream.ReadAsync(buffer, ct)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    long newDone = Interlocked.Add(ref done, read);
                    percent?.Report(totalBytes == 0 ? 1.0 : (double)newDone / totalBytes);
                }
            }
        });
        await Task.WhenAll(tasks);

        percent?.Report(1.0);
    }

    /// <summary>
    /// Volledig terugzetten vanaf een ontvangen .pctbackup-zipbestand
    /// (gebruikt na rechtstreekse netwerkoverdracht).
    /// </summary>
    public async Task RestoreZipAsync(string packageZipPath, bool overwriteExisting, IProgress<double>? percent, CancellationToken ct, IProgress<string>? currentFileProgress = null)
    {
        string stagingDir = Path.Combine(Path.GetTempPath(), "PCTransfer11_restore_" + Guid.NewGuid().ToString("N"));
        try
        {
            _log.Report("Pakket uitpakken ...");
            ZipFile.ExtractToDirectory(packageZipPath, stagingDir);

            var manifest = LoadManifest(stagingDir);
            await RestoreFromFolderAsync(stagingDir, manifest, filePackagePaths: null, settingsAppIds: null, overwriteExisting, percent, ct, currentFileProgress);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    /// <summary>
    /// Zet (een selectie van) een back-upmap terug op deze pc.
    /// <paramref name="filePackagePaths"/> en <paramref name="settingsAppIds"/> geven aan
    /// welke items teruggezet moeten worden (vergelijk met FileEntry.PackagePath resp.
    /// SettingsEntry.AppId uit het manifest) - geef <c>null</c> mee om alles terug te zetten.
    /// </summary>
    public async Task RestoreFromFolderAsync(
        string backupFolderPath,
        PackageManifest manifest,
        ISet<string>? filePackagePaths,
        ISet<string>? settingsAppIds,
        bool overwriteExisting,
        IProgress<double>? percent,
        CancellationToken ct,
        IProgress<string>? currentFileProgress = null)
    {
        string origin = manifest.ToolVersion.Contains("android", StringComparison.OrdinalIgnoreCase)
            ? $"Android-toestel ({manifest.CreatedByMachine})"
            : $"Windows-pc ({manifest.CreatedByMachine})";
        _log.Report($"Back-up gemaakt op: {origin}, {manifest.CreatedAtUtc:g} UTC.");

        var filesToRestore = manifest.Files
            .Where(f => filePackagePaths == null || filePackagePaths.Contains(f.PackagePath))
            .ToList();
        var settingsToRestore = manifest.Settings
            .Where(s => settingsAppIds == null || settingsAppIds.Contains(s.AppId))
            .ToList();

        // ------------------------------------------------------------------
        // Eén gecombineerde UAC-aanvraag vooraf, in plaats van een apart
        // UAC-venster per onderdeel: dat is veel gebruiksvriendelijker dan de
        // gebruiker meerdere keren na elkaar te laten bevestigen. Hier wordt
        // dus eerst bepaald wát er allemaal aan adminrechten nodig gaat zijn
        // (ontbrekende profielmappen, Wifi, netwerkadapter) en dat wordt in
        // één keer uitgevoerd vóórdat de eigenlijke terugzet-stappen beginnen.
        // ------------------------------------------------------------------
        var resolvedDestinations = filesToRestore.ToDictionary(
            f => f,
            f => FileSelectionItem.ResolveKnownFolder(f.KnownFolderId) ?? f.OriginalPath);

        // ---- Schijfruimte vooraf controleren, per bestemmingsschijf ----
        // (bv. Documenten en Video's kunnen theoretisch op verschillende
        // schijven staan) - voorkomt een onverwacht "schijf vol" halverwege
        // een grote overdracht.
        _log.Report("Bestanden tellen en grootte bepalen ...");
        var neededBytesPerDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileEntry in filesToRestore)
        {
            string source = Path.Combine(backupFolderPath, fileEntry.PackagePath);
            string dest = resolvedDestinations[fileEntry];
            string? root = Path.GetPathRoot(Path.GetFullPath(dest));
            if (string.IsNullOrEmpty(root)) continue;

            long size = Directory.Exists(source) ? GetDirectorySize(source)
                      : File.Exists(source) ? new FileInfo(source).Length : 0;
            neededBytesPerDrive[root] = neededBytesPerDrive.GetValueOrDefault(root) + size;
        }
        foreach (var (driveRoot, needed) in neededBytesPerDrive)
            PackageBuilder.CheckFreeDiskSpace(driveRoot, needed);

        // ---- Bytes-gebaseerde voortgang i.p.v. "aantal items" ----
        // Zonder dit telt een back-up met 50 kleine instellingen en 2 GB aan
        // foto's elk item even zwaar mee, wat een misleidende voortgangsbalk
        // geeft. Hier wordt (net als bij het maken van een back-up) de
        // werkelijke hoeveelheid te kopiëren bytes bepaald.
        long totalFileBytes = neededBytesPerDrive.Values.Sum();
        long totalSettingsBytes = 0;
        foreach (var settingsEntry in settingsToRestore)
        {
            string dataDir = Path.Combine(backupFolderPath, PackageBuilder.SettingsFolderName, settingsEntry.AppId, "data");
            totalSettingsBytes += Directory.Exists(dataDir) ? GetDirectorySize(dataDir) : 64 * 1024L;
        }
        var tracker = new PackageBuilder.ByteProgressTracker(totalFileBytes + totalSettingsBytes, percent, currentFileProgress);

        var foldersToEnsure = resolvedDestinations.Values
            .Select(FindMissingParent)
            .Where(p => p != null)
            .Distinct()
            .Cast<string>()
            .ToList();

        var wifiEntry = settingsToRestore.FirstOrDefault(s => s.AppId == "windows_wifi" && s.HasCustomData);
        var adapterEntry = settingsToRestore.FirstOrDefault(s => s.AppId == "windows_network_adapter" && s.HasCustomData);
        string? wifiDataSource = wifiEntry != null
            ? Path.Combine(backupFolderPath, PackageBuilder.SettingsFolderName, wifiEntry.AppId, "data") : null;
        string? adapterDataSource = adapterEntry != null
            ? Path.Combine(backupFolderPath, PackageBuilder.SettingsFolderName, adapterEntry.AppId, "data") : null;

        bool wifiHandledByBatch = false, adapterHandledByBatch = false;
        if (foldersToEnsure.Count > 0 || wifiDataSource != null || adapterDataSource != null)
        {
            bool batchOk = await ElevatedNetworkHelper.RunElevatedBatchAsync(
                foldersToEnsure,
                wifiDataSource != null ? "import" : null, wifiDataSource,
                adapterDataSource != null ? "import" : null, adapterDataSource,
                requestTrust: false,
                ct, _log);

            // Ook als de UAC-prompt geweigerd is, geldt de rest van het terugzetten
            // gewoon door (bestanden/instellingen die geen adminrechten nodig
            // hebben blijven werken) - alleen deze twee onderdelen slaan we dan
            // over in de hoofdlus hieronder, in plaats van opnieuw te herlanceren.
            wifiHandledByBatch = batchOk && wifiDataSource != null;
            adapterHandledByBatch = batchOk && adapterDataSource != null;
        }

        int restoredFiles = 0, skippedFiles = 0, failedFiles = 0;
        int restoredSettings = 0, skippedSettings = 0, failedSettings = 0;

        foreach (var fileEntry in filesToRestore)
        {
            ct.ThrowIfCancellationRequested();
            string source = Path.Combine(backupFolderPath, fileEntry.PackagePath);
            if (!Directory.Exists(source) && !File.Exists(source))
            {
                _log.Report($"Overslaan, ontbreekt in back-up: {fileEntry.DisplayName}");
                skippedFiles++;
                continue;
            }

            // BELANGRIJK: voor een bekende map (Documenten, Downloads, ...)
            // wordt het pad hier OPNIEUW opgezocht op DEZE (doel-)machine in
            // plaats van het letterlijke pad van de bronmachine te hergebruiken.
            // Anders gaat dit fout zodra de gebruikersnaam op de nieuwe pc
            // anders is dan op de oude (bv. terugzetten op een account met een
            // andere naam) - de map moet naar het HUIDIGE profiel wijzen,
            // ongeacht hoe dat heet. Alleen voor een door de gebruiker zelf
            // toegevoegde, aangepaste map (geen KnownFolderId) wordt het
            // letterlijke opgeslagen pad gebruikt, bij gebrek aan alternatief.
            string destinationPath = resolvedDestinations[fileEntry];

            _log.Report($"Terugzetten: {fileEntry.DisplayName} -> {destinationPath}");
            try
            {
                if (Directory.Exists(source))
                    await CopyDirectoryAsync(source, destinationPath, overwriteExisting, tracker, ct);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    tracker.ReportCurrentFile($"Terugzetten: {Path.GetFileName(destinationPath)}");
                    File.Copy(source, destinationPath, overwriteExisting);
                    tracker.AddCopiedBytes(new FileInfo(source).Length);
                }
                restoredFiles++;
            }
            catch (UnauthorizedAccessException)
            {
                _log.Report($"Fout bij terugzetten van '{fileEntry.DisplayName}': geen toegang tot " +
                            $"'{destinationPath}'.");

                string? missingProfileRoot = FindMissingParent(destinationPath);
                bool recovered = false;
                if (missingProfileRoot != null)
                {
                    _log.Report($"    De map '{missingProfileRoot}' bestaat nog niet. Een gewone (niet-" +
                                "elevated) app mag zoiets niet zomaar aanmaken direct onder C:\\Users - dat " +
                                "lost ik nu automatisch op met één eenmalige UAC-vraag ...");
                    bool created = await ElevatedNetworkHelper.EnsureFolderExistsElevatedAsync(missingProfileRoot, ct, _log);
                    if (created)
                    {
                        try
                        {
                            if (Directory.Exists(source))
                                await CopyDirectoryAsync(source, destinationPath, overwriteExisting, tracker, ct);
                            else
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                                tracker.ReportCurrentFile($"Terugzetten: {Path.GetFileName(destinationPath)}");
                                File.Copy(source, destinationPath, overwriteExisting);
                                tracker.AddCopiedBytes(new FileInfo(source).Length);
                            }
                            _log.Report($"    Alsnog gelukt: {fileEntry.DisplayName}.");
                            recovered = true;
                        }
                        catch (Exception ex2)
                        {
                            _log.Report($"    Nog steeds mislukt na het aanmaken van de map: {ex2.Message}");
                        }
                    }
                }
                else
                {
                    foreach (string hint in DiagnoseAccessDenied(destinationPath))
                        _log.Report("    " + hint);
                }

                if (recovered) restoredFiles++; else failedFiles++;
            }
            catch (Exception ex)
            {
                _log.Report($"Fout bij terugzetten van '{fileEntry.DisplayName}': {ex.Message}");
                failedFiles++;
            }
        }

        bool anyWindowsRegistrySettingsRestored = false;
        foreach (var settingsEntry in settingsToRestore)
        {
            ct.ThrowIfCancellationRequested();
            var app = KnownApps.GetAll().FirstOrDefault(a => a.Id == settingsEntry.AppId);

            string appStagingDir = Path.Combine(backupFolderPath, PackageBuilder.SettingsFolderName, settingsEntry.AppId);
            string dataSource = Path.Combine(appStagingDir, "data");

            bool settingsHandled = false;

            if (settingsEntry.AppId == "windows_wifi" && wifiHandledByBatch)
            {
                _log.Report("Wifi-netwerken: al verwerkt tijdens de gezamenlijke UAC-stap hierboven.");
                settingsHandled = true;
            }
            else if (settingsEntry.AppId == "windows_network_adapter" && adapterHandledByBatch)
            {
                _log.Report("Netwerkadapter/proxy: al verwerkt tijdens de gezamenlijke UAC-stap hierboven.");
                settingsHandled = true;
            }
            else if (settingsEntry.HasCustomData && app?.CustomImport != null)
            {
                _log.Report($"Instellingen terugzetten: {settingsEntry.DisplayName} ...");
                await app.CustomImport(dataSource, ct, _log);
                tracker.AddCopiedBytes(Directory.Exists(dataSource) ? GetDirectorySize(dataSource) : 64 * 1024L);
                settingsHandled = true;
            }
            else if (settingsEntry.HasDataFolder)
            {
                string? dataDestination = app?.ResolveDataFolder();
                if (dataDestination == null)
                {
                    _log.Report($"Kan doelmap voor '{settingsEntry.DisplayName}' niet bepalen op deze pc " +
                                "(applicatie waarschijnlijk niet geïnstalleerd) - overgeslagen.");
                }
                else
                {
                    _log.Report($"Instellingen terugzetten: {settingsEntry.DisplayName} ...");
                    await CopyDirectoryAsync(dataSource, dataDestination, overwriteExisting, tracker, ct);
                    settingsHandled = true;
                }
            }

            if (settingsEntry.HasRegistryExport)
            {
                var regFiles = Directory.Exists(appStagingDir)
                    ? Directory.GetFiles(appStagingDir, "registry_*.reg")
                        .Concat(Directory.GetFiles(appStagingDir, "registry.reg")) // oudere back-ups (vóór meervoudige sleutels)
                        .Distinct()
                        .OrderBy(f => f)
                    : Enumerable.Empty<string>();

                bool any = false;
                foreach (string regFile in regFiles)
                {
                    any = true;
                    _log.Report($"Registerinstellingen terugzetten: {settingsEntry.DisplayName} ...");
                    await ImportRegistryFileAsync(regFile, ct);
                }
                if (!any)
                    _log.Report($"Registerbestand voor '{settingsEntry.DisplayName}' ontbreekt in de back-up - overgeslagen.");
                else
                    settingsHandled = true;

                if (any) anyWindowsRegistrySettingsRestored = true;

                if (any && settingsEntry.AppId == "windows_network_drives")
                {
                    _log.Report("Let op: de netwerkschijfletters zijn teruggezet, maar de bijbehorende " +
                                "inloggegevens staan (versleuteld) in Windows Credential Manager en gaan nooit " +
                                "mee - vul het wachtwoord bij de eerste keer verbinden opnieuw in.");
                }
            }

            if (settingsHandled) restoredSettings++; else skippedSettings++;
        }

        if (anyWindowsRegistrySettingsRestored)
            WindowsSettingsRefresher.TryApplyImmediately(_log);

        percent?.Report(1.0);

        _log.Report("");
        _log.Report("===== Samenvatting =====");
        _log.Report($"Bestanden: {restoredFiles} teruggezet, {skippedFiles} overgeslagen, {failedFiles} mislukt.");
        _log.Report($"Instellingen: {restoredSettings} teruggezet, {skippedSettings} overgeslagen, {failedSettings} mislukt.");
        if (failedFiles > 0 || failedSettings > 0)
            _log.Report("Let op: niet alles is gelukt - zie de foutregels hierboven voor details.");
        _log.Report("Terugzetten voltooid.");
    }

    /// <summary>
    /// Directory.Exists() geeft bij ELKE fout (ook "access denied") gewoon
    /// stil false terug - je kan dus niet onderscheiden of een map echt niet
    /// bestaat, of dat hij bestaat maar niet toegankelijk is. File.GetAttributes
    /// gooit wél een specifieke exception per situatie, dus die gebruiken we
    /// hier om dat verschil betrouwbaar vast te stellen.
    /// </summary>
    private static bool ReliablyExists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (DirectoryNotFoundException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return true; } // bestaat wél, alleen geen toegang
        catch { return true; } // bij twijfel niet onterecht "ontbreekt" concluderen
    }

    /// <summary>
    /// Zoekt, startend bij <paramref name="path"/> omhoog, de eerste
    /// bovenliggende map die echt ontbreekt (bv. de hele profielmap
    /// "C:\Users\Microsoft" als die nog nooit is aangemaakt) - of null als
    /// alle bovenliggende mappen wél bestaan (dan is de oorzaak iets anders,
    /// zoals Controlled Folder Access).
    /// </summary>
    private static string? FindMissingParent(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        return (parent != null && !ReliablyExists(parent)) ? parent : null;
    }

    /// <summary>
    /// Probeert bij "Access denied" de meest waarschijnlijke oorzaak te
    /// benoemen, zodat de gebruiker niet naar losse .NET-foutmeldingen hoeft
    /// te raden. Let op: "de virusscanner uitzetten" of "de .exe aan de
    /// gewone uitsluitingslijst toevoegen" schakelt Controlled Folder
    /// Access NIET uit - dat is een aparte, eigen toegestane-apps-lijst.
    /// </summary>
    private static IReadOnlyList<string> DiagnoseAccessDenied(string destinationPath)
    {
        var hints = new List<string>();

        try
        {
            bool isReparsePoint =
                (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                && (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0;

            if (isReparsePoint)
            {
                hints.Add("Dit pad is een link/junction (vaak OneDrive 'Bekende mappen synchroniseren') - " +
                          "controleer of OneDrive actief en aangemeld is, want dan wijst deze map eigenlijk naar " +
                          "een OneDrive-map en kan een gewone kopieeractie daar soms op stuklopen.");
            }
            else
            {
                hints.Add("Dit is bijna altijd Windows Beveiliging's Ransomware-bescherming (Controlled Folder " +
                          "Access) - LET OP: dit is een ANDERE lijst dan de gewone virusscanner-uitsluitingen die " +
                          "je al had ingesteld. Probeer eerst de knop 'PCTransfer11 toevoegen aan Windows' " +
                          "vertrouwde apps' op tab 'Over'. Werkt dat niet, ga dan naar Windows Beveiliging > " +
                          "Virus- en bedreigingsbeveiliging > 'Ransomware-bescherming beheren' (een aparte pagina) " +
                          "> zet daar 'Beheerde mapstoegang' uit.");
            }
        }
        catch (Exception ex)
        {
            hints.Add($"(kon niet automatisch verder diagnosticeren: {ex.Message})");
        }

        return hints;
    }

    private async Task CopyDirectoryAsync(string sourceDir, string destinationDir, bool overwrite, PackageBuilder.ByteProgressTracker tracker, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDir);
        // EnumerateDirectories/EnumerateFiles i.p.v. GetDirectories/GetFiles: die
        // laatste twee lezen EERST de volledige boom in het geheugen in vóórdat
        // er iets teruggegeven wordt - bij een grote map (bv. Video's/Foto's)
        // voelt dat als een bevroren venster zonder enige voortgang. Enumerate
        // geeft resultaten direct door zodra ze gevonden worden.
        foreach (string dirPath in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dirPath.Replace(sourceDir, destinationDir));
        }
        // Bestanden gelijktijdig kopiëren (begrensd door _copyLimiter) i.p.v.
        // strikt één voor één - vooral bij veel kleine bestanden (foto's!) op
        // een SSD een merkbare versnelling.
        var fileTasks = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).Select(async filePath =>
        {
            ct.ThrowIfCancellationRequested();
            string dest = filePath.Replace(sourceDir, destinationDir);
            if (!overwrite && File.Exists(dest))
                return;
            await _copyLimiter.WaitAsync(ct);
            try
            {
                await CopyFileStreamedAsync(filePath, dest, tracker, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { /* bestand in gebruik - overslaan */ }
            catch (UnauthorizedAccessException) { /* geen toegang - overslaan */ }
            finally { _copyLimiter.Release(); }
        });
        await Task.WhenAll(fileTasks);
    }

    /// <summary>
    /// Kopieert één bestand met een handmatige stream-loop (i.p.v. File.Copy)
    /// zodat het CancellationToken tijdens het kopiëren zelf gecontroleerd
    /// wordt - de "Stop"-knop reageert zo ook meteen bij grote bestanden - en
    /// zodat de bytes-gebaseerde voortgangsbalk per gelezen blok kan bijwerken.
    /// </summary>
    private static async Task CopyFileStreamedAsync(string sourceFile, string destFile, PackageBuilder.ByteProgressTracker tracker, CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024; // 1 MB
        string? destDir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        tracker.ReportCurrentFile($"Terugzetten: {Path.GetFileName(sourceFile)}");

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
    /// Importeert een .reg-bestand met het ingebouwde Windows-hulpprogramma
    /// reg.exe. Werkt alleen op HKEY_CURRENT_USER-sleutels (die vereisen
    /// geen adminrechten), zoals ook alleen HKCU wordt geëxporteerd.
    /// </summary>
    private async Task ImportRegistryFileAsync(string regFile, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"import \"{regFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return;

            // Streams altijd uitlezen terwijl op afsluiten gewacht wordt - anders
            // kan een volle pijplijnbuffer reg.exe (en dus deze aanroep) laten
            // vasthangen, net als eerder bij netsh (zie ElevatedNetworkHelper).
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);

            if (process.ExitCode != 0)
                _log.Report("Waarschuwing: het importeren van de registerinstellingen is mogelijk niet volledig gelukt.");
        }
        catch (Exception ex)
        {
            _log.Report($"Kon registerbestand niet importeren: {ex.Message}");
        }
    }

    private static long GetDirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* enkel bestand niet te lezen - negeren voor deze schatting */ }
            }
        }
        catch { /* map niet te lezen - schatting blijft op wat al geteld was */ }
        return total;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort opruimen van tijdelijke bestanden */ }
    }
}
