using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCTransfer11.Services;

/// <summary>
/// Sommige netwerkinstellingen (vast IP/DNS/gateway per adapter, de
/// systeembrede proxy, en Wifi-wachtwoorden in klare tekst) staan in
/// HKEY_LOCAL_MACHINE of zijn alleen met adminrechten op te vragen. De rest
/// van PCTransfer11 draait bewust zonder adminrechten (asInvoker), dus voor
/// alleen déze onderdelen herlanceert de app zichzelf kort, onzichtbaar en
/// met een UAC-verzoek (via "runas") om precies dat ene commando uit te
/// voeren. De hoofd-app wacht daarna gewoon op het resultaat.
///
/// De elevated (onzichtbare) instantie schrijft precies wat er gebeurde weg
/// naar een klein statusbestand in de doel-/bronmap; de hoofd-app leest dat
/// terug en logt het letterlijk, zodat altijd zichtbaar is of de UAC-prompt
/// is geaccepteerd én wat daarna wel/niet is gelukt (in plaats van alleen
/// een stille aanname op basis van de afsluitcode).
///
/// BELANGRIJK VOOR GEBRUIKSVRIENDELIJKHEID: als een actie meerdere
/// UAC-onderdelen tegelijk nodig heeft (bv. Wifi + netwerkadapter + een
/// ontbrekende profielmap), gebruik dan <see cref="RunElevatedBatchAsync"/>
/// zodat dat in ÉÉN UAC-venster gebeurt in plaats van meerdere na elkaar.
/// De losse Run...Async-methoden hieronder blijven bestaan voor gevallen
/// waarin maar één onderdeel tegelijk relevant is (bv. de losse
/// "Windows vertrouwen"-knop op tab "Over").
///
/// Herkent zichzelf via de command-line-argumenten "--elevated-export" /
/// "--elevated-import" / "--elevated-batch" - zie
/// <see cref="TryHandleElevatedArgs"/>, die helemaal vooraan in App.xaml.cs
/// wordt aangeroepen.
/// </summary>
public static class ElevatedNetworkHelper
{
    private const string ExportFlag = "--elevated-export";
    private const string ImportFlag = "--elevated-import";
    private const string TrustFlag = "--elevated-trust";
    private const string MkdirFlag = "--elevated-mkdir";
    private const string BatchFlag = "--elevated-batch";
    private const string SessionFlag = "--elevated-session";
    private const string StatusFileName = "_elevated_status.txt";

    /// <summary>Beschrijft alle mogelijke adminrechten-onderdelen voor ÉÉN gecombineerde UAC-aanvraag.</summary>
    private sealed class BatchJob
    {
        public List<string> EnsureFolders { get; set; } = new();
        /// <summary>"export" of "import", of null als Wifi niet in deze batch zit.</summary>
        public string? WifiAction { get; set; }
        public string? WifiDir { get; set; }
        /// <summary>"export" of "import", of null als de netwerkadapter niet in deze batch zit.</summary>
        public string? AdapterAction { get; set; }
        public string? AdapterDir { get; set; }
        public bool Trust { get; set; }
        public string? UserName { get; set; }
    }

    // ---------------------------------------------------------------
    // Aanroepen vanuit de (niet-elevated) hoofd-app
    // ---------------------------------------------------------------

    // ---------------------------------------------------------------
    // Doorlopende sessie: ÉÉN UAC-venster bij het opstarten van de app,
    // gebruikt voor alle adminrechten-taken tijdens de rest van de sessie -
    // in plaats van telkens opnieuw een UAC-venster tijdens het back-uppen/
    // terugzetten. Werkt via een kortstondig, onzichtbaar elevated
    // hulpproces dat blijft draaien en luistert op een named pipe.
    // ---------------------------------------------------------------

    private static NamedPipeClientStream? _sessionPipe;
    private static readonly object _sessionLock = new();

    /// <summary>
    /// Of dit proces zelf al met adminrechten draait (bv. omdat het manifest
    /// "requireAdministrator" is, of de gebruiker de app zelf als admin
    /// startte). Zo ja, dan is de hele UAC-omleiding hieronder (zelf
    /// herlanceren via "runas", named pipe, apart om toestemming vragen)
    /// overbodig én overbodig risicovol (dubbele/verwarrende UAC-vensters,
    /// een pipe-verbinding tussen twee toch-al-elevated processen die om
    /// een andere reden kan mislukken) - dan wordt gewoon meteen
    /// rechtstreeks uitgevoerd, zonder er nog een tweede proces bij te halen.
    /// </summary>
    /// <summary>Publiek zichtbare versie van <see cref="IsRunningElevated"/>, voor gebruik door de UI (bv. om de opstartvraag over te slaan).</summary>
    public static bool IsProcessElevated() => IsRunningElevated();

    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false; // bij twijfel: aannemen van niet-elevated, dan gaat de bestaande (werkende) UAC-weg gewoon door
        }
    }

    /// <summary>Of er nu een doorlopende, al geaccepteerde adminrechten-sessie actief is.</summary>
    public static bool HasPersistentSession
    {
        get { lock (_sessionLock) { return _sessionPipe is { IsConnected: true }; } }
    }

    /// <summary>
    /// Toont ÉÉN UAC-venster en start, als dat geaccepteerd wordt, een
    /// kortstondig onzichtbaar hulpproces dat de rest van de sessie actief
    /// blijft en via een named pipe adminrechten-taken uitvoert zonder
    /// verdere UAC-onderbrekingen. Roep dit bv. bij het opstarten van de
    /// app aan (of via een knop), niet per se pas wanneer een taak het
    /// nodig heeft.
    /// </summary>
    public static async Task<bool> StartPersistentElevatedSessionAsync(CancellationToken ct, IProgress<string> log)
    {
        if (HasPersistentSession) return true;

        if (IsRunningElevated())
        {
            log.Report("Deze app draait al met adminrechten (requireAdministrator) - een aparte sessie/UAC-venster is niet nodig.");
            return true;
        }

        string pipeName = "PCTransfer11_session_" + Guid.NewGuid().ToString("N");
        log.Report("Windows toont nu één UAC-venster voor de rest van deze sessie - accepteer dat venster om " +
                    "latere UAC-onderbrekingen tijdens back-uppen/terugzetten te voorkomen ...");

        Process? process;
        try
        {
            string exePath = Environment.ProcessPath
                              ?? Process.GetCurrentProcess().MainModule?.FileName
                              ?? throw new InvalidOperationException("Kan het pad naar PCTransfer11.exe niet bepalen.");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{SessionFlag} \"{pipeName}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            log.Report("UAC-prompt geannuleerd - er wordt tijdens deze sessie per keer om adminrechten gevraagd zodra dat nodig is.");
            return false;
        }
        catch (Exception ex)
        {
            log.Report($"Kon geen doorlopende sessie met adminrechten starten: {ex.Message}");
            return false;
        }

        if (process == null)
        {
            log.Report("Kon het hulpproces niet starten.");
            return false;
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15000, ct); // max 15s wachten tot UAC geaccepteerd is en de pipe opent
        }
        catch (Exception ex)
        {
            log.Report($"Geen adminrechten gekregen (UAC geweigerd/geannuleerd, of time-out): {ex.Message}");
            pipe.Dispose();
            return false;
        }

        lock (_sessionLock) { _sessionPipe = pipe; }
        log.Report("UAC geaccepteerd - Wifi/netwerkadapter/ontbrekende mappen worden voor de rest van deze " +
                    "sessie zonder verdere UAC-vragen verwerkt.");
        return true;
    }

    /// <summary>Sluit de doorlopende sessie netjes af (bv. bij het afsluiten van de app).</summary>
    public static void StopPersistentElevatedSession()
    {
        lock (_sessionLock)
        {
            if (_sessionPipe == null) return;
            try { WriteFramed(_sessionPipe, "STOP"); } catch { /* best effort */ }
            try { _sessionPipe.Dispose(); } catch { /* best effort */ }
            _sessionPipe = null;
        }
    }

    /// <summary>Stuurt een job naar de al actieve sessie en wacht op het statusresultaat.</summary>
    private static async Task<List<string>> SendJobToSessionAsync(BatchJob job, CancellationToken ct)
    {
        NamedPipeClientStream? pipe;
        lock (_sessionLock) { pipe = _sessionPipe; }
        if (pipe is not { IsConnected: true })
            return new List<string> { "Geen actieve sessie meer - opnieuw als losse UAC-aanvraag proberen." };

        try
        {
            string json = JsonSerializer.Serialize(job);
            byte[] data = Encoding.UTF8.GetBytes(json);
            await pipe.WriteAsync(BitConverter.GetBytes(data.Length), ct);
            await pipe.WriteAsync(data, ct);
            await pipe.FlushAsync(ct);

            byte[] lenBuf = new byte[4];
            await pipe.ReadExactlyAsync(lenBuf, ct);
            int len = BitConverter.ToInt32(lenBuf, 0);
            byte[] respBuf = new byte[len];
            await pipe.ReadExactlyAsync(respBuf, ct);
            return JsonSerializer.Deserialize<List<string>>(Encoding.UTF8.GetString(respBuf)) ?? new List<string>();
        }
        catch (Exception ex)
        {
            lock (_sessionLock) { _sessionPipe = null; }
            return new List<string> { $"Sessie met adminrechten is weggevallen: {ex.Message}" };
        }
    }

    /// <summary>Schrijft een lengte-voorafgegaan UTF8-bericht (gedeeld frameformaat voor de sessie-pipe).</summary>
    private static void WriteFramed(Stream s, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        s.Write(BitConverter.GetBytes(data.Length));
        s.Write(data);
        s.Flush();
    }

    /// <summary>Leest een lengte-voorafgegaan UTF8-bericht, of null als de pipe gesloten is.</summary>
    private static string? ReadFramed(Stream s)
    {
        byte[] lenBuf = new byte[4];
        try { s.ReadExactly(lenBuf); }
        catch { return null; }
        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 50_000_000) return null;
        byte[] data = new byte[len];
        s.ReadExactly(data);
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>
    /// Voert alle opgegeven adminrechten-onderdelen uit in ÉÉN UAC-venster in
    /// plaats van een apart venster per onderdeel. Geef alleen mee wat voor
    /// déze actie relevant is; laat de rest op null/leeg staan.
    /// </summary>
    public static async Task<bool> RunElevatedBatchAsync(
        IEnumerable<string> ensureFolders,
        string? wifiAction, string? wifiDir,
        string? adapterAction, string? adapterDir,
        bool requestTrust,
        CancellationToken ct, IProgress<string> log)
    {
        var job = new BatchJob
        {
            EnsureFolders = ensureFolders.Distinct().ToList(),
            WifiAction = wifiAction,
            WifiDir = wifiDir,
            AdapterAction = adapterAction,
            AdapterDir = adapterDir,
            Trust = requestTrust,
            UserName = Environment.UserName
        };

        bool needsElevation = job.EnsureFolders.Count > 0 || job.WifiAction != null
                               || job.AdapterAction != null || job.Trust;
        if (!needsElevation) return true;

        if (IsRunningElevated())
        {
            log.Report("Dit proces draait al met adminrechten - wordt direct uitgevoerd, geen UAC nodig.");
            var directStatus = new List<string>();
            ExecuteBatchJob(job, directStatus);
            foreach (string line in directStatus)
                log.Report("    " + line);
            return true;
        }

        log.Report(HasPersistentSession
            ? "Sessie met adminrechten: actief - wordt via de al lopende sessie verwerkt, geen nieuwe UAC-vraag."
            : "Sessie met adminrechten: niet actief - er wordt nu een losse UAC-aanvraag getoond.");

        if (HasPersistentSession)
        {
            var sessionStatus = await SendJobToSessionAsync(job, ct);
            foreach (string line in sessionStatus)
                log.Report("    " + line);
            return true;
        }

        // Eigen, uniek (GUID-)tempmapje per aanroep - voorkomt dat twee
        // gelijktijdige runs (of resten van een eerdere, afgebroken run)
        // elkaars job-/statusbestand overschrijven.
        string batchDir = Path.Combine(Path.GetTempPath(), "PCTransfer11_batch_" + Guid.NewGuid().ToString("N"));
        string jobFile = Path.Combine(batchDir, "job.json");

        try
        {
            Directory.CreateDirectory(batchDir);
            File.WriteAllText(jobFile, JsonSerializer.Serialize(job));
        }
        catch (Exception ex)
        {
            log.Report($"Kon de adminrechten-actie niet voorbereiden: {ex.Message}");
            return false;
        }

        var onderdelen = new List<string>();
        if (job.EnsureFolders.Count > 0) onderdelen.Add("ontbrekende profielmap(pen) aanmaken");
        if (job.WifiAction != null) onderdelen.Add("Wifi-netwerken");
        if (job.AdapterAction != null) onderdelen.Add("netwerkadapter/proxy");
        if (job.Trust) onderdelen.Add("Windows vertrouwen");

        log.Report($"Windows toont nu ÉÉN UAC-venster voor alle onderdelen die adminrechten nodig hebben " +
                    $"in deze actie ({string.Join(", ", onderdelen)}) - accepteer dat venster om door te gaan ...");

        bool launched = await LaunchElevatedSelfAsync($"{BatchFlag} \"{jobFile}\"", ct, log);
        ReportStatusFile(batchDir, log);
        TryDeleteDirectory(batchDir);

        log.Report(launched
            ? "UAC geaccepteerd - alle onderdelen hierboven zijn in één keer verwerkt (zie eventuele " +
              "foutregels per onderdeel hierboven als iets alsnog niet lukte)."
            : "Geen adminrechten gekregen (UAC geweigerd/geannuleerd) - alle onderdelen die dat nodig hadden " +
              "zijn overgeslagen.");
        return launched;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort opruimen van tijdelijke bestanden */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Herlanceert deze .exe met adminrechten (UAC-prompt) om de gevraagde
    /// export uit te voeren. Logt expliciet of de UAC-prompt is geaccepteerd
    /// en wat er daadwerkelijk is opgehaald. Weigert de gebruiker de
    /// UAC-prompt, dan wordt dat gemeld en - alleen voor Wifi - alsnog een
    /// back-up zonder wachtwoord geprobeerd (dat lukt namelijk wel zonder
    /// adminrechten).
    /// </summary>
    public static async Task<bool> RunElevatedExportAsync(string kind, string destDir, CancellationToken ct, IProgress<string> log)
    {
        Directory.CreateDirectory(destDir);
        log.Report("Windows toont nu een UAC-venster ('Wil je toestaan dat deze app wijzigingen aanbrengt?') " +
                    "voor de netwerkinstellingen - accepteer dat venster om door te gaan ...");

        bool launched = await LaunchElevatedSelfAsync($"{ExportFlag} {kind} \"{destDir}\"", ct, log);
        ReportStatusFile(destDir, log);

        bool produced = kind switch
        {
            "adapter" => File.Exists(Path.Combine(destDir, "netcfg.txt")),
            "wifi" => Directory.Exists(destDir) && Directory.GetFiles(destDir, "*.xml").Length > 0,
            _ => false
        };

        if (produced)
        {
            log.Report(kind == "adapter"
                ? "UAC geaccepteerd: netwerkadapter- en proxy-instellingen zijn opgehaald."
                : "UAC geaccepteerd: Wifi-netwerken (mét wachtwoord) zijn opgehaald.");
            return true;
        }

        if (launched)
            log.Report("UAC is geaccepteerd, maar er kon niets worden opgehaald - zie de foutregel(s) hierboven.");

        if (!launched && kind == "wifi")
        {
            log.Report("Geen adminrechten gekregen (UAC geweigerd/geannuleerd) - Wifi-netwerken worden nu zonder " +
                       "wachtwoord opgehaald (dat lukt wel zonder UAC) ...");
            return await NetworkSettingsExporter.ExportWifiProfilesAsync(destDir, ct, log);
        }

        if (!launched)
            log.Report("Geen adminrechten gekregen (UAC geweigerd/geannuleerd) - dit onderdeel wordt overgeslagen.");

        return false;
    }

    /// <summary>
    /// Herlanceert deze .exe met adminrechten (UAC-prompt) om zichzelf toe te
    /// voegen aan Windows' vertrouwde apps: zowel de "toegestane apps"-lijst
    /// van Controlled Folder Access (Ransomware-bescherming) als de gewone
    /// Windows Defender-uitsluitingen. Dit is de kern van "Windows laten
    /// vertrouwen" - hierna hoeft de gebruiker dat niet meer met de hand in
    /// Windows Beveiliging te doen.
    ///
    /// Let op: dit maakt de "Onbekende uitgever"-melding in het UAC-venster
    /// zelf niet weg - dat vereist een echt code-signing-certificaat op de
    /// .exe, wat geen software-aanpassing kan regelen.
    /// </summary>
    public static async Task<bool> RequestWindowsTrustAsync(CancellationToken ct, IProgress<string> log)
    {
        if (IsRunningElevated())
        {
            log.Report("Dit proces draait al met adminrechten - wordt direct uitgevoerd, geen UAC nodig.");
            var directStatus = new List<string>();
            ExecuteBatchJob(new BatchJob { Trust = true }, directStatus);
            foreach (string line in directStatus)
                log.Report("    " + line);
            return true;
        }

        if (HasPersistentSession)
        {
            var sessionStatus = await SendJobToSessionAsync(new BatchJob { Trust = true }, ct);
            foreach (string line in sessionStatus)
                log.Report("    " + line);
            log.Report("Verwerkt via de al actieve sessie met adminrechten (geen nieuwe UAC-vraag nodig).");
            return true;
        }

        log.Report("Windows toont nu een UAC-venster om PCTransfer11 toe te voegen aan de vertrouwde apps " +
                    "(Controlled Folder Access + Windows Defender-uitsluitingen) - accepteer dat venster ...");

        string trustDir = Path.Combine(Path.GetTempPath(), "PCTransfer11_trust_" + Guid.NewGuid().ToString("N"));
        bool launched = await LaunchElevatedSelfAsync($"{TrustFlag} \"{trustDir}\"", ct, log);
        ReportStatusFile(trustDir, log);
        TryDeleteDirectory(trustDir);

        if (launched)
        {
            log.Report("UAC geaccepteerd - PCTransfer11.exe is toegevoegd aan Controlled Folder Access en de " +
                        "Windows Defender-uitsluitingen. Dit moet je hierna niet meer handmatig instellen.");
            return true;
        }

        log.Report("Geen adminrechten gekregen (UAC geweigerd/geannuleerd) - er is niets aangepast.");
        return false;
    }
    /// <summary>
    /// Herlanceert deze .exe met adminrechten (UAC-prompt) om een ontbrekende
    /// map (meestal de hele gebruikersprofielmap, bv. "C:\Users\Microsoft")
    /// aan te maken en de huidige gebruiker er expliciet volledige rechten op
    /// te geven. Nodig omdat een gewone, niet-elevated app nooit zomaar een
    /// nieuwe map direct onder "C:\Users" mag aanmaken - dat is Windows'
    /// eigen beveiliging, geen bug in deze app.
    /// </summary>
    public static async Task<bool> EnsureFolderExistsElevatedAsync(string path, CancellationToken ct, IProgress<string> log)
    {
        string userName = Environment.UserName;

        if (IsRunningElevated())
        {
            log.Report("Dit proces draait al met adminrechten - wordt direct uitgevoerd, geen UAC nodig.");
            var directStatus = new List<string>();
            ExecuteBatchJob(new BatchJob { EnsureFolders = new List<string> { path }, UserName = userName }, directStatus);
            foreach (string line in directStatus)
                log.Report("    " + line);
            bool directOk = Directory.Exists(path);
            log.Report(directOk
                ? $"'{path}' is aangemaakt."
                : $"Aanmaken van '{path}' is niet gelukt.");
            return directOk;
        }

        if (HasPersistentSession)
        {
            var sessionStatus = await SendJobToSessionAsync(
                new BatchJob { EnsureFolders = new List<string> { path }, UserName = userName }, ct);
            foreach (string line in sessionStatus)
                log.Report("    " + line);
            bool sessionOk = Directory.Exists(path);
            log.Report(sessionOk
                ? $"Verwerkt via de al actieve sessie met adminrechten: '{path}' is aangemaakt."
                : $"Aanmaken van '{path}' is niet gelukt (via de actieve sessie).");
            return sessionOk;
        }

        log.Report($"Windows toont nu een UAC-venster om '{path}' aan te maken en '{userName}' er volledige " +
                    "rechten op te geven - accepteer dat venster ...");

        bool launched = await LaunchElevatedSelfAsync($"{MkdirFlag} \"{path}\" \"{userName}\"", ct, log);
        ReportStatusFile(path, log);

        bool ok = Directory.Exists(path);
        log.Report(ok
            ? $"UAC geaccepteerd: '{path}' is aangemaakt en toegankelijk gemaakt voor {userName}."
            : $"Aanmaken van '{path}' is niet gelukt{(launched ? "" : " (UAC geweigerd/geannuleerd)")}.");
        return ok;
    }

    /// <summary>Zelfde principe als <see cref="RunElevatedExportAsync"/>, maar dan voor terugzetten.</summary>
    public static async Task RunElevatedImportAsync(string kind, string sourceDir, CancellationToken ct, IProgress<string> log)
    {
        if (!Directory.Exists(sourceDir)) return;

        log.Report("Windows toont nu een UAC-venster voor het terugzetten van deze netwerkinstelling - " +
                    "accepteer dat venster om door te gaan ...");
        bool launched = await LaunchElevatedSelfAsync($"{ImportFlag} {kind} \"{sourceDir}\"", ct, log);
        ReportStatusFile(sourceDir, log);

        if (launched)
        {
            log.Report(kind == "adapter"
                ? "UAC geaccepteerd: netwerkadapter- en proxy-instellingen zijn teruggezet."
                : "UAC geaccepteerd: Wifi-netwerken zijn teruggezet.");
            return;
        }

        if (kind == "wifi")
        {
            log.Report("Geen adminrechten gekregen (UAC geweigerd/geannuleerd) - Wifi-profielen worden nu zonder " +
                       "wachtwoord toegevoegd (je vult het wachtwoord dan zelf eenmalig in) ...");
            await NetworkSettingsExporter.ImportWifiProfilesAsync(sourceDir, ct, log);
            return;
        }

        log.Report("Geen adminrechten gekregen (UAC geweigerd/geannuleerd) - dit onderdeel is niet teruggezet.");
    }

    /// <summary>Leest het statusbestand van de elevated helper terug in het logboek (en ruimt het daarna op).</summary>
    private static void ReportStatusFile(string dir, IProgress<string> log)
    {
        string statusFile = Path.Combine(dir, StatusFileName);
        if (!File.Exists(statusFile)) return;
        try
        {
            foreach (string line in File.ReadAllLines(statusFile))
                if (!string.IsNullOrWhiteSpace(line))
                    log.Report("    " + line);
            File.Delete(statusFile);
        }
        catch { /* best effort */ }
    }

    private static async Task<bool> LaunchElevatedSelfAsync(string arguments, CancellationToken ct, IProgress<string> log)
    {
        Process? process = null;
        try
        {
            string exePath = Environment.ProcessPath
                              ?? Process.GetCurrentProcess().MainModule?.FileName
                              ?? throw new InvalidOperationException("Kan het pad naar PCTransfer11.exe niet bepalen.");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,   // vereist voor "runas"
                Verb = "runas",           // vraagt om de UAC-prompt
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync(ct);
            // De elevated helper meldt zijn eigen resultaat via het statusbestand
            // (zie ReportStatusFile); de afsluitcode zegt alleen dat het proces
            // gestart en weer gestopt is (dus dat UAC is geaccepteerd).
            return true;
        }
        catch (OperationCanceledException)
        {
            try { process?.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: gebruiker weigerde de UAC-prompt
        {
            log.Report("UAC-prompt geannuleerd door de gebruiker.");
            return false;
        }
        catch (Exception ex)
        {
            log.Report($"Kon niet als administrator uitvoeren: {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------
    // De elevated (onzichtbare, kortstondige) helper-modus zelf
    // ---------------------------------------------------------------

    /// <summary>
    /// Wordt helemaal vooraan in App.OnStartup aangeroepen. Geeft true terug
    /// als de opgegeven command-line-argumenten een elevated-helperverzoek
    /// waren (in dat geval is de actie al uitgevoerd en moet de app direct
    /// afsluiten zonder een venster te tonen).
    /// </summary>
    public static bool TryHandleElevatedArgs(string[] args)
    {
        if (args.Length == 2 && args[0] == TrustFlag)
        {
            RunTrustWorker(args[1]);
            return true;
        }
        if (args.Length == 2 && args[0] == BatchFlag)
        {
            RunBatchWorker(args[1]);
            return true;
        }
        if (args.Length == 2 && args[0] == SessionFlag)
        {
            RunPersistentSessionServer(args[1]);
            return true;
        }

        if (args.Length != 3) return false;

        string flag = args[0];
        string kind = args[1];
        string dir = args[2];

        if (flag == ExportFlag)
        {
            RunExportWorker(kind, dir);
            return true;
        }
        if (flag == ImportFlag)
        {
            RunImportWorker(kind, dir);
            return true;
        }
        if (flag == MkdirFlag)
        {
            RunMkdirWorker(path: kind, userName: dir);
            return true;
        }
        return false;
    }

    /// <summary>
    /// De elevated (onzichtbare) kant van de doorlopende sessie: blijft
    /// draaien en luistert op de named pipe totdat de hoofd-app "STOP"
    /// stuurt, de pipe wegvalt (bv. omdat de hoofd-app is afgesloten), of er
    /// een onherstelbare fout optreedt. Voert ondertussen zoveel jobs uit
    /// als de hoofd-app stuurt, zonder ooit opnieuw een UAC-venster te tonen.
    /// </summary>
    private static void RunPersistentSessionServer(string pipeName)
    {
        try
        {
            // BELANGRIJK: dit proces draait elevated (hoge integriteit), maar
            // de hoofd-app draait bewust NIET elevated (medium integriteit).
            // Windows' Mandatory Integrity Control staat standaard niet toe
            // dat een lager-geprivilegieerd proces verbinding maakt met een
            // pipe die door een elevated proces is aangemaakt ("access
            // denied", geen UAC-weigering) - vandaar hieronder expliciet een
            // lagere integriteitslabel + volledige toegang instellen via
            // Win32 (bewust geen extra NuGet-package zoals
            // System.IO.Pipes.AccessControl - dat past niet bij het
            // "geen externe dependencies"-uitgangspunt van dit project).
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.None);
            ApplyLowIntegrityPipeSecurity(server);

            // Als de hoofd-app om wat voor reden dan ook niet binnen 20s verbindt
            // (bv. zelf al gestopt, of iets anders ging mis), moet dit
            // onzichtbare hulpproces niet voor altijd blijven hangen.
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                server.WaitForConnectionAsync(connectCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (true)
            {
                string? jobJson = ReadFramed(server);
                if (jobJson == null || jobJson == "STOP") break;

                var status = new List<string>();
                try
                {
                    var job = JsonSerializer.Deserialize<BatchJob>(jobJson);
                    if (job != null) ExecuteBatchJob(job, status);
                    else status.Add("Kon de ontvangen taak niet lezen.");
                }
                catch (Exception ex)
                {
                    status.Add($"Onverwachte fout: {ex.Message}");
                }

                WriteFramed(server, JsonSerializer.Serialize(status));
            }
        }
        catch
        {
            // De sessie stopt gewoon (bv. omdat de hoofd-app is afgesloten) -
            // de hoofd-app valt dan vanzelf terug op losse UAC-aanvragen per keer.
        }
    }

    /// <summary>
    /// Verlaagt het integriteitsniveau van een net aangemaakte named pipe naar
    /// "Low", zodat een niet-elevated proces (medium integriteit) er wél
    /// verbinding mee kan maken. Rechtstreeks via Win32 (advapi32.dll) i.p.v.
    /// het NuGet-package "System.IO.Pipes.AccessControl", om geen extra
    /// externe dependency toe te voegen aan dit project.
    /// </summary>
    private static void ApplyLowIntegrityPipeSecurity(NamedPipeServerStream pipe)
    {
        const int DaclSecurityInformation = 0x00000004;
        const int LabelSecurityInformation = 0x00000010;
        const string sddl = "D:(A;;GA;;;WD)S:(ML;;;LW)"; // Everyone: volledige toegang; label: Low (dus ook door medium/low bereikbaar)

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr sd, out _))
            throw new InvalidOperationException($"Kon de pipe-beveiliging niet voorbereiden (Win32-fout {Marshal.GetLastWin32Error()}).");

        try
        {
            if (!SetKernelObjectSecurity(pipe.SafePipeHandle, DaclSecurityInformation | LabelSecurityInformation, sd))
                throw new InvalidOperationException($"Kon de pipe-beveiliging niet toepassen (Win32-fout {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            LocalFree(sd);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor, uint stringSDRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(SafeHandle handle, int securityInformation, IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static void RunBatchWorker(string jobFilePath)
    {
        var status = new List<string>();
        try
        {
            if (!File.Exists(jobFilePath))
            {
                status.Add("Jobbestand ontbreekt - niets gedaan.");
                return;
            }

            var job = JsonSerializer.Deserialize<BatchJob>(File.ReadAllText(jobFilePath));
            if (job == null)
            {
                status.Add("Jobbestand kon niet worden gelezen - niets gedaan.");
                return;
            }

            ExecuteBatchJob(job, status);
        }
        catch (Exception ex)
        {
            status.Add($"Onverwachte fout: {ex.Message}");
        }
        finally
        {
            TryWriteStatus(Path.GetDirectoryName(jobFilePath) ?? Path.GetTempPath(), status);
        }
    }

    /// <summary>
    /// De daadwerkelijke uitvoering van een <see cref="BatchJob"/> - los van
    /// hoe de job binnenkwam (jobbestand voor een eenmalige UAC-aanvraag, of
    /// een bericht via de named pipe van een doorlopende sessie), en los van
    /// waar het resultaat naartoe gaat.
    /// </summary>
    private static void ExecuteBatchJob(BatchJob job, List<string> status)
    {
        foreach (string folder in job.EnsureFolders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                status.Add($"Map aangemaakt: {folder}");
                if (!string.IsNullOrEmpty(job.UserName))
                    status.Add(RunPowerShell($"icacls '{folder}' /grant '{job.UserName}':(OI)(CI)F /T"));
            }
            catch (Exception ex)
            {
                status.Add($"Map '{folder}' aanmaken mislukt: {ex.Message}");
            }
        }

        if (job.WifiDir != null)
        {
            if (job.WifiAction == "export")
            {
                Directory.CreateDirectory(job.WifiDir);
                status.Add(RunNetsh($"wlan export profile folder=\"{job.WifiDir}\" key=clear"));
            }
            else if (job.WifiAction == "import")
            {
                status.AddRange(ReplayWifiProfiles(job.WifiDir));
            }
        }

        if (job.AdapterDir != null)
        {
            if (job.AdapterAction == "export")
            {
                Directory.CreateDirectory(job.AdapterDir);
                status.Add(RunNetshRedirectToFile("interface dump", Path.Combine(job.AdapterDir, "netcfg.txt")));
                status.Add(RunNetshRedirectToFile("winhttp show proxy", Path.Combine(job.AdapterDir, "proxy_systeem.txt")));
            }
            else if (job.AdapterAction == "import")
            {
                status.AddRange(ReplayNetcfgFile(job.AdapterDir));
            }
        }

        if (job.Trust)
        {
            string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                status.Add("Kon het eigen .exe-pad niet bepalen - Windows vertrouwen niet aangepast.");
            }
            else
            {
                status.Add(RunPowerShell($"Add-MpPreference -ControlledFolderAccessAllowedApplications '{exePath}'"));
                status.Add(RunPowerShell($"Add-MpPreference -ExclusionProcess '{exePath}'"));
                status.Add(RunPowerShell($"Add-MpPreference -ExclusionPath '{exePath}'"));
            }
        }
    }

    private static void RunMkdirWorker(string path, string userName)
    {
        var status = new List<string>();
        try
        {
            Directory.CreateDirectory(path);
            status.Add($"Map aangemaakt: {path}");
            status.Add(RunPowerShell($"icacls '{path}' /grant '{userName}':(OI)(CI)F /T"));
        }
        catch (Exception ex)
        {
            status.Add($"Onverwachte fout: {ex.Message}");
        }
        finally
        {
            TryWriteStatus(path, status);
        }
    }

    private static void RunTrustWorker(string statusDir)
    {
        var status = new List<string>();
        try
        {
            string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                status.Add("Kon het eigen .exe-pad niet bepalen - niets aangepast.");
            }
            else
            {
                // Voegt PCTransfer11.exe toe aan de "toegestane apps"-lijst van
                // Controlled Folder Access (Ransomware-bescherming) én aan de
                // gewone Windows Defender-uitsluitingen. Gebruikt de ingebouwde
                // Defender PowerShell-module - die zit standaard op elke
                // Windows 10/11-installatie, er is niets extra's voor nodig.
                status.Add(RunPowerShell($"Add-MpPreference -ControlledFolderAccessAllowedApplications '{exePath}'"));
                status.Add(RunPowerShell($"Add-MpPreference -ExclusionProcess '{exePath}'"));
                status.Add(RunPowerShell($"Add-MpPreference -ExclusionPath '{exePath}'"));
            }
        }
        catch (Exception ex)
        {
            status.Add($"Onverwachte fout: {ex.Message}");
        }
        finally
        {
            TryWriteStatus(statusDir, status);
        }
    }

    /// <summary>Voert een PowerShell-commando uit en geeft een leesbare statusregel terug (geen exceptions).</summary>
    private static string RunPowerShell(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return $"powershell {command} -> kon niet worden gestart.";

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit((int)NetshTimeout.TotalMilliseconds);

            if (!exited)
            {
                TryKill(process);
                return $"powershell {command} -> TIME-OUT na {(int)NetshTimeout.TotalSeconds}s (proces afgebroken).";
            }

            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, (int)NetshTimeout.TotalMilliseconds);
            string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            return process.ExitCode == 0
                ? $"{command} -> gelukt."
                : $"{command} -> FOUT (code {process.ExitCode}): {stderr.Trim()}";
        }
        catch (Exception ex)
        {
            return $"{command} -> FOUT: {ex.Message}";
        }
    }

    private static void RunExportWorker(string kind, string destDir)
    {
        var status = new List<string>();
        try
        {
            Directory.CreateDirectory(destDir);
            switch (kind)
            {
                case "adapter":
                    status.Add(RunNetshRedirectToFile("interface dump", Path.Combine(destDir, "netcfg.txt")));
                    status.Add(RunNetshRedirectToFile("winhttp show proxy", Path.Combine(destDir, "proxy_systeem.txt")));
                    break;
                case "wifi":
                    status.Add(RunNetsh($"wlan export profile folder=\"{destDir}\" key=clear"));
                    break;
                default:
                    status.Add($"Onbekend onderdeel '{kind}' - niets gedaan.");
                    break;
            }
        }
        catch (Exception ex)
        {
            status.Add($"Onverwachte fout tijdens ophalen: {ex.Message}");
        }
        finally
        {
            TryWriteStatus(destDir, status);
        }
    }

    private static void RunImportWorker(string kind, string sourceDir)
    {
        var status = new List<string>();
        try
        {
            switch (kind)
            {
                case "adapter":
                    status.AddRange(ReplayNetcfgFile(sourceDir));
                    break;
                case "wifi":
                    status.AddRange(ReplayWifiProfiles(sourceDir));
                    break;
                default:
                    status.Add($"Onbekend onderdeel '{kind}' - niets gedaan.");
                    break;
            }
        }
        catch (Exception ex)
        {
            status.Add($"Onverwachte fout tijdens terugzetten: {ex.Message}");
        }
        finally
        {
            TryWriteStatus(sourceDir, status);
        }
    }

    /// <summary>
    /// Speelt een eerder gemaakte "netsh interface dump" (netcfg.txt) regel
    /// voor regel af i.p.v. in één keer met "netsh -f": dan stopt niet het
    /// hele script zodra één regel verwijst naar een netwerkadapter die op
    /// déze pc een andere naam heeft (heel gebruikelijk bij andere hardware
    /// of een andere Windows-versie) - de overige, wél toepasbare regels
    /// (bv. de systeemproxy) gaan dan gewoon door, en elke mislukte regel
    /// wordt apart gemeld.
    ///
    /// BELANGRIJK: "netsh dump" gebruikt "pushd &lt;context&gt;" / "popd" om
    /// aan te geven binnen welk subonderdeel (bv. "interface ipv4") de
    /// daaropvolgende kale commando's als "reset" of "set global ..." moeten
    /// worden uitgevoerd - dat is dus "netsh interface ipv4 reset", niet los
    /// "netsh reset". Die context wordt hier zelf bijgehouden bij het los per
    /// regel afspelen, anders faalt letterlijk elke regel met "the following
    /// command was not found".
    /// </summary>
    private static List<string> ReplayNetcfgFile(string sourceDir)
    {
        var status = new List<string>();
        string netcfg = Path.Combine(sourceDir, "netcfg.txt");
        if (!File.Exists(netcfg))
        {
            status.Add("netcfg.txt ontbreekt in de back-up - niets teruggezet.");
            return status;
        }

        string[] lines = File.ReadAllLines(netcfg);
        var contextStack = new List<string>();
        int lineNumber = 0;
        foreach (string rawLine in lines)
        {
            lineNumber++;
            string cmd = rawLine.Trim();
            if (cmd.Length == 0 || cmd.StartsWith("#"))
                continue; // lege regels en commentaar uit de dump overslaan

            if (cmd.StartsWith("pushd", StringComparison.OrdinalIgnoreCase))
            {
                string sub = cmd.Length > 5 ? cmd[5..].Trim() : "";
                string current = contextStack.Count > 0 ? contextStack[^1] : "";
                contextStack.Add(string.IsNullOrEmpty(current) ? sub : $"{current} {sub}");
                continue;
            }
            if (cmd.Equals("popd", StringComparison.OrdinalIgnoreCase))
            {
                if (contextStack.Count > 0) contextStack.RemoveAt(contextStack.Count - 1);
                continue;
            }

            string context = contextStack.Count > 0 ? contextStack[^1] : "";
            string fullCommand = string.IsNullOrEmpty(context) ? cmd : $"{context} {cmd}";
            status.Add($"[regel {lineNumber}] " + RunNetsh(fullCommand));
        }
        return status;
    }

    /// <summary>Voegt alle Wifi-profielen (.xml) uit de back-up toe via netsh.</summary>
    private static List<string> ReplayWifiProfiles(string sourceDir)
    {
        var status = new List<string>();
        if (!Directory.Exists(sourceDir))
        {
            status.Add("Bronmap ontbreekt - niets teruggezet.");
            return status;
        }

        var xmlFiles = Directory.GetFiles(sourceDir, "*.xml");
        if (xmlFiles.Length == 0)
            status.Add("Geen Wifi-profielen (.xml) gevonden in de back-up.");
        foreach (string xml in xmlFiles)
            status.Add(RunNetsh($"wlan add profile filename=\"{xml}\" user=all"));
        return status;
    }

    private static void TryWriteStatus(string dir, List<string> status)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, StatusFileName), status);
        }
        catch { /* best effort - als zelfs dit mislukt, is er verder ook niets te doen */ }
    }

    /// <summary>Voert een netsh-commando uit en geeft een leesbare statusregel terug (geen exceptions).</summary>
    private static string RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return $"netsh {arguments} -> kon niet worden gestart.";

            // BELANGRIJK: stdout én stderr moeten allebei gelijktijdig (async)
            // worden uitgelezen vóórdat op afsluiten wordt gewacht. Zodra maar
            // één van de twee omgeleide streams niet wordt uitgelezen, kan de
            // pijplijnbuffer daarvan vollopen (bv. omdat "netsh -f" bij het
            // terugzetten per regel output geeft) - dan blokkeert netsh op het
            // schrijven en blijft dit commando (en dus de hele terugzetactie)
            // voor altijd hangen. Vandaar ook een harde tijdslimiet als vangnet.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit((int)NetshTimeout.TotalMilliseconds);

            if (!exited)
            {
                TryKill(process);
                return $"netsh {arguments} -> TIME-OUT na {(int)NetshTimeout.TotalSeconds}s (proces afgebroken).";
            }

            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, (int)NetshTimeout.TotalMilliseconds);
            string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            if (process.ExitCode == 0)
                return $"netsh {arguments} -> gelukt.";

            // netsh schrijft foutdetails vaak naar de standaarduitvoer, niet naar
            // stderr (die kan dan leeg blijven terwijl het commando toch faalt) -
            // daarom hier allebei samenvoegen zodat de echte reden zichtbaar is.
            string detail = string.Join(" | ", new[] { stderr.Trim(), stdout.Trim() }
                .Where(s => s.Length > 0));
            if (detail.Length == 0) detail = "(geen foutdetails van netsh ontvangen)";
            return $"netsh {arguments} -> FOUT (code {process.ExitCode}): {detail}";
        }
        catch (Exception ex)
        {
            return $"netsh {arguments} -> FOUT: {ex.Message}";
        }
    }

    /// <summary>Zelfde als <see cref="RunNetsh"/>, maar schrijft de standaarduitvoer naar een bestand.</summary>
    private static string RunNetshRedirectToFile(string arguments, string outputFile)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return $"netsh {arguments} -> kon niet worden gestart.";

            // Zie de toelichting in RunNetsh: stdout én stderr moeten gelijktijdig
            // worden uitgelezen (niet na elkaar), anders kan er een deadlock
            // ontstaan zodra één van de twee pijplijnbuffers vol raakt.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit((int)NetshTimeout.TotalMilliseconds);

            if (!exited)
            {
                TryKill(process);
                return $"netsh {arguments} -> TIME-OUT na {(int)NetshTimeout.TotalSeconds}s (proces afgebroken).";
            }

            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, (int)NetshTimeout.TotalMilliseconds);
            string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            if (process.ExitCode != 0)
                return $"netsh {arguments} -> FOUT (code {process.ExitCode}): {stderr.Trim()}";

            File.WriteAllText(outputFile, stdout);
            return $"netsh {arguments} -> gelukt ({stdout.Length} tekens weggeschreven naar {Path.GetFileName(outputFile)}).";
        }
        catch (Exception ex)
        {
            return $"netsh {arguments} -> FOUT: {ex.Message}";
        }
    }

    /// <summary>Harde bovengrens per netsh-aanroep, zodat een onverwacht vastlopend commando de hele terugzet-/back-upactie niet voor altijd blokkeert.</summary>
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(45);

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }
}
