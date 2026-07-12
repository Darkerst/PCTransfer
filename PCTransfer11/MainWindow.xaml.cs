using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using PCTransfer11.Models;
using PCTransfer11.Services;

namespace PCTransfer11;

public partial class MainWindow : Window
{
    private readonly List<FileSelectionItem> _fileItems = new();
    private readonly List<AppProfile> _appProfiles = new();
    private readonly List<RestoreSelectionItem> _restoreItems = new();

    private string? _selectedBackupFolder;
    private PackageManifest? _selectedBackupManifest;

    private readonly NetworkReceiver _networkReceiver;
    private readonly NetworkSender _networkSender = new();

    private CancellationTokenSource? _discoveryResponderCts;

    private readonly Progress<string> _logProgress;
    private string? _logFilePath;
    private readonly Progress<double> _percentProgress;
    private readonly Progress<string> _currentFileProgress;
    private string? _lastExtractedFolder;

    private CancellationTokenSource? _operationCts;

    public MainWindow()
    {
        InitializeComponent();

        // Logboek ook als bestand wegschrijven (naast alleen op het scherm),
        // zodat je het niet meer met de hand hoeft te kopiëren als je iets
        // wilt terugzoeken of delen - één bestand per sessie, met tijdstempel.
        try
        {
            string logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCTransfer11", "Logs");
            Directory.CreateDirectory(logsDir);
            _logFilePath = Path.Combine(logsDir, $"pctransfer11_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        }
        catch
        {
            _logFilePath = null; // kon geen logbestand aanmaken - dan gewoon alleen op het scherm loggen
        }

        _logProgress = new Progress<string>(Log);
        _percentProgress = new Progress<double>(p =>
        {
            TransferProgressBar.Value = p;
            ProgressPercentText.Text = $"{p * 100:0}%";
        });
        _currentFileProgress = new Progress<string>(message => CurrentFileText.Text = message);
        _networkReceiver = new NetworkReceiver(_logProgress);

        InitializeFileItems();
        InitializeAppProfiles();

        FilesItemsControl.ItemsSource = _fileItems;

        var appsView = (ListCollectionView)CollectionViewSource.GetDefaultView(_appProfiles);
        appsView.GroupDescriptions.Clear();
        appsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AppProfile.Category)));
        appsView.CustomSort = Comparer<AppProfile>.Create((a, b) =>
            string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase));
        AppsItemsControl.ItemsSource = appsView;

        UpdateReceiverInfoText();
        StartDiscoveryResponderIfReceiver();
        InitializeAboutTab();
        if (_logFilePath != null)
            Log($"Logboek van deze sessie wordt opgeslagen in: {_logFilePath}");

        Closing += (_, _) =>
        {
            _discoveryResponderCts?.Cancel();
            ElevatedNetworkHelper.StopPersistentElevatedSession();
        };
        Loaded += async (_, _) => await OfferPersistentElevatedSessionAsync();
    }

    /// <summary>
    /// Vraagt bij het opstarten ÉÉN keer of de gebruiker nu alvast adminrechten
    /// wil geven voor de rest van de sessie, zodat latere Wifi/netwerkadapter/
    /// ontbrekende-mappen-taken tijdens back-uppen/terugzetten geen aparte
    /// UAC-onderbreking meer geven. Weigert de gebruiker (of de UAC-prompt),
    /// dan valt de app terug op het vertrouwde gedrag van per keer vragen
    /// zodra dat nodig is - niets breekt daardoor.
    /// </summary>
    private async Task OfferPersistentElevatedSessionAsync()
    {
        if (ElevatedNetworkHelper.IsProcessElevated())
        {
            Log("Deze app draait al met adminrechten (requireAdministrator) - geen aparte UAC-vraag nodig voor deze sessie.");
            return;
        }

        var result = MessageBox.Show(
            "Wil je nu alvast eenmalig adminrechten geven voor deze hele sessie?\n\n" +
            "Dat voorkomt latere UAC-onderbrekingen tijdens het back-uppen/terugzetten van Wifi-netwerken, " +
            "de netwerkadapter of een ontbrekende profielmap.\n\n" +
            "Kies je 'Nee', dan wordt er (zoals voorheen) per keer om adminrechten gevraagd zodra dat nodig is.",
            "PCTransfer11 - Adminrechten voor deze sessie",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await ElevatedNetworkHelper.StartPersistentElevatedSessionAsync(cts.Token, _logProgress);
        }
        catch (Exception ex)
        {
            Log($"Kon geen doorlopende sessie met adminrechten starten: {ex.Message}");
        }
    }

    private void InitializeFileItems()
    {
        _fileItems.Add(FileSelectionItem.ForSpecialFolder("Documenten", Environment.SpecialFolder.MyDocuments));
        _fileItems.Add(FileSelectionItem.ForSpecialFolder("Afbeeldingen", Environment.SpecialFolder.MyPictures));
        _fileItems.Add(FileSelectionItem.ForSpecialFolder("Bureaublad", Environment.SpecialFolder.DesktopDirectory));
        _fileItems.Add(FileSelectionItem.ForSpecialFolder("Video's", Environment.SpecialFolder.MyVideos));
        _fileItems.Add(FileSelectionItem.ForSpecialFolder("Muziek", Environment.SpecialFolder.MyMusic));

        _fileItems.Add(FileSelectionItem.ForDownloads("Downloads"));

        // Let op: bewust GEEN "openbare/gedeelde" (Public, C:\Users\Public\...) mappen
        // meer toevoegen. Er wordt alleen nog van het eigen gebruikersprofiel
        // gebackupt, niet van het openbare/gedeelde profiel.

        foreach (var item in _fileItems)
            item.IsChecked = item.Exists;
    }

    private void InitializeAppProfiles()
    {
        _appProfiles.AddRange(KnownApps.GetAll());
        foreach (var app in _appProfiles)
            app.IsChecked = app.IsAvailable && !app.RequiresElevation;
    }

    private void InitializeAboutTab()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = version != null
            ? $"Versie {version.Major}.{version.Minor}.{version.Build}"
            : "Versie onbekend";
    }

    private void AppsSelectAll_Click(object sender, RoutedEventArgs e) => SetAllAppChecks(true);
    private void AppsSelectNone_Click(object sender, RoutedEventArgs e) => SetAllAppChecks(false);

    private async void TrustApp_Click(object sender, RoutedEventArgs e)
    {
        var ct = BeginOperation();
        try
        {
            await ElevatedNetworkHelper.RequestWindowsTrustAsync(ct, _logProgress);
        }
        catch (OperationCanceledException)
        {
            Log("Gestopt door gebruiker.");
        }
        catch (Exception ex)
        {
            Log($"Fout: {ex.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    private void SetAllAppChecks(bool value)
    {
        foreach (var app in _appProfiles.Where(a => a.IsAvailable))
            app.IsChecked = value;
        CollectionViewSource.GetDefaultView(_appProfiles).Refresh();
    }

    /// <summary>Herkent aan manifest.ToolVersion of een back-up van de Android- of de Windows-versie komt.</summary>
    private static string DescribeOrigin(PackageManifest manifest) =>
        manifest.ToolVersion.Contains("android", StringComparison.OrdinalIgnoreCase)
            ? $"een Android-toestel ('{manifest.CreatedByMachine}')"
            : $"een Windows-pc ('{manifest.CreatedByMachine}')";

    /// <summary>Maakt een door de gebruiker/afzender opgegeven naam veilig voor gebruik als mapnaam.</summary>
    private static string SanitizeForPath(string name)
    {
        string result = name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            result = result.Replace(c, '_');
        return string.IsNullOrWhiteSpace(result) ? "onbekend-apparaat" : result;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? logsDir = _logFilePath != null ? Path.GetDirectoryName(_logFilePath) : null;
            if (logsDir != null && Directory.Exists(logsDir))
                Process.Start(new ProcessStartInfo { FileName = logsDir, UseShellExecute = true });
            else
                Log("Kon de logboekmap niet vinden.");
        }
        catch (Exception ex)
        {
            Log($"Kon de logboekmap niet openen: {ex.Message}");
        }
    }

    private void OpenExtractedFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastExtractedFolder != null && Directory.Exists(_lastExtractedFolder))
                Process.Start(new ProcessStartInfo { FileName = _lastExtractedFolder, UseShellExecute = true });
            else
                Log("Kon de map met uitgepakte bestanden niet vinden.");
        }
        catch (Exception ex)
        {
            Log($"Kon de map niet openen: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        });

        if (_logFilePath != null)
        {
            try { File.AppendAllText(_logFilePath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); }
            catch { /* best effort - het logbestand mag de app nooit laten crashen */ }
        }
    }

    // ================= TAB 1: SELECTEREN =================

    private void AddCustomFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Kies een map om mee over te zetten" };
        if (dialog.ShowDialog() == true)
        {
            var item = new FileSelectionItem
            {
                DisplayName = Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar)),
                Path = dialog.FolderName,
                IsChecked = true
            };
            _fileItems.Add(item);
            FilesItemsControl.ItemsSource = null;
            FilesItemsControl.ItemsSource = _fileItems;
        }
    }

    // ================= TAB 2: OVERZETTEN =================

    private void ModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (NetworkPanel == null || BackupPanel == null) return; // tijdens initialisatie van XAML
        bool network = ModeNetworkRadio.IsChecked == true;
        NetworkPanel.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        BackupPanel.Visibility = network ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RoleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (ReceiverPanel == null || SenderPanel == null) return;
        bool isReceiver = RoleReceiverRadio.IsChecked == true;
        ReceiverPanel.Visibility = isReceiver ? Visibility.Visible : Visibility.Collapsed;
        SenderPanel.Visibility = isReceiver ? Visibility.Collapsed : Visibility.Visible;

        if (isReceiver)
            StartDiscoveryResponderIfReceiver();
        else
            _discoveryResponderCts?.Cancel();
    }

    private void StartDiscoveryResponderIfReceiver()
    {
        _discoveryResponderCts?.Cancel();
        _discoveryResponderCts = new CancellationTokenSource();
        _ = _networkReceiver.RunDiscoveryResponderAsync(NetworkReceiver.DefaultTcpPort, _discoveryResponderCts.Token);
    }

    private void UpdateReceiverInfoText()
    {
        string ip = GetLocalIPv4() ?? "onbekend";
        ReceiverInfoText.Text = $"Deze pc heet '{Environment.MachineName}' en is te bereiken op {ip}. " +
                                 "Zorg dat beide pc's op hetzelfde (Wifi-)netwerk zitten en klik daarna hieronder op 'Start ontvangen'. " +
                                 "Windows kan de eerste keer om firewall-toestemming vragen - klik dan op 'Toegang toestaan'.";
    }

    private static string? GetLocalIPv4()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private async void StartReceive_Click(object sender, RoutedEventArgs e)
    {
        string pin = NetworkCrypto.GeneratePin();
        ReceivePinText.Text = $"PIN: {pin}";
        ReceivePinText.Visibility = Visibility.Visible;
        ReceivePinHintText.Visibility = Visibility.Visible;
        Log($"PIN voor deze overdracht: {pin} - deel dit met de verzendende pc.");

        var ct = BeginOperation(switchToProgressTab: false); // pas wisselen zodra er echt verbinding is - tot die tijd moet de PIN hierboven zichtbaar blijven
        try
        {
            string tempPackagePath = Path.Combine(Path.GetTempPath(), "PCTransfer11_ontvangen.pctbackup");
            await _networkReceiver.ReceiveOnceAsync(tempPackagePath, pin, _percentProgress, ct, _currentFileProgress,
                onConnected: () => MainTabControl.SelectedIndex = 2,
                // Zodra de zendende kant zijn toestelnaam heeft doorgegeven (al vóór de
                // bulkoverdracht start) vullen we die meteen in het naamveld in - maar
                // alleen als de gebruiker daar zelf nog niks heeft ingetypt, zodat een
                // handmatig gekozen naam nooit wordt overschreven.
                onRemoteDeviceName: name => Dispatcher.Invoke(() =>
                {
                    if (string.IsNullOrWhiteSpace(ReceiveLabelTextBox.Text))
                        ReceiveLabelTextBox.Text = name;
                }));

            // De ontvangen back-up bewaren in een map met de naam die je hierboven
            // hebt ingevuld (handmatig, of automatisch voorgesteld op basis van de
            // toestelnaam die de zendende kant net heeft doorgegeven, bv. "Pixel 9").
            string label = string.IsNullOrWhiteSpace(ReceiveLabelTextBox.Text) ? "Onbekend apparaat" : ReceiveLabelTextBox.Text.Trim();
            string safeLabel = SanitizeForPath(label);
            string devicesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PCTransfer11 ontvangen back-ups", safeLabel);
            Directory.CreateDirectory(devicesRoot);
            string savedPackagePath = Path.Combine(devicesRoot, $"PCTransfer_{DateTime.Now:yyyyMMdd_HHmmss}.pctbackup");
            File.Copy(tempPackagePath, savedPackagePath, overwrite: true);
            Log($"Back-up bewaard in: {savedPackagePath}");
            try { File.Delete(tempPackagePath); } catch { /* best effort - tijdelijk bestand, niet kritisch */ }

            var result = MessageBox.Show(
                $"Het pakket is ontvangen en bewaard in:\n{savedPackagePath}\n\nNu meteen uitpakken naar een map " +
                $"'{label}' in je Downloads-map?",
                "PCTransfer11", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                string downloadsFolder = FileSelectionItem.ResolveKnownFolder("Downloads")
                                          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                string extractDir = Path.Combine(downloadsFolder, safeLabel);

                Log($"Uitpakken naar: {extractDir} ...");
                await Task.Run(() => PackageRestorer.ExtractZipWithProgressAsync(savedPackagePath, extractDir, _percentProgress, _currentFileProgress, ct), ct);

                _lastExtractedFolder = extractDir;
                OpenExtractedFolderButton.Content = $"Map openen: {extractDir}";
                OpenExtractedFolderButton.Visibility = Visibility.Visible;

                // De ingepakte (zip-)versie is niet meer nodig zodra 'm is
                // uitgepakt - de map waarin die stond (inclusief eventuele
                // andere back-ups van dit apparaat) wordt daarom opgeruimd.
                try
                {
                    Directory.Delete(devicesRoot, recursive: true);
                    Log($"Ingepakte versie opgeruimd: {devicesRoot}");
                }
                catch (Exception ex)
                {
                    Log($"Kon de ingepakte versie niet opruimen: {ex.Message}");
                }

                MessageBox.Show($"Uitpakken voltooid:\n{extractDir}", "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Ontvangst gestopt door gebruiker.");
        }
        catch (Exception ex)
        {
            Log($"Fout tijdens ontvangst: [{ex.GetType().Name}] {ex.Message}");
            MessageBox.Show($"Er ging iets mis tijdens de ontvangst:\n{ex.Message}", "PCTransfer11",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ReceivePinText.Visibility = Visibility.Collapsed;
            ReceivePinHintText.Visibility = Visibility.Collapsed;
            EndOperation();
        }
    }

    private async void DiscoverReceivers_Click(object sender, RoutedEventArgs e)
    {
        var ct = BeginOperation();
        Log("Zoeken naar pc's op het netwerk ...");
        try
        {
            var found = await NetworkSender.DiscoverAsync(2500, ct);
            ReceiversComboBox.ItemsSource = found;
            if (found.Count > 0)
            {
                ReceiversComboBox.SelectedIndex = 0;
                Log($"{found.Count} pc('s) gevonden.");
            }
            else
            {
                Log("Geen pc's gevonden. Controleer of de andere pc op 'Start ontvangen' staat en op hetzelfde netwerk zit.");
            }
        }
        catch (OperationCanceledException)
        {
            Log("Zoeken gestopt door gebruiker.");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void StartSend_Click(object sender, RoutedEventArgs e)
    {
        string? ip = (ReceiversComboBox.SelectedItem as DiscoveredReceiver)?.IpAddress;
        int port = (ReceiversComboBox.SelectedItem as DiscoveredReceiver)?.TcpPort ?? NetworkReceiver.DefaultTcpPort;

        if (string.IsNullOrWhiteSpace(ip))
            ip = ManualIpTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("Kies een gevonden pc of vul een IP-adres in.", "PCTransfer11",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string pin = SendPinTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pin))
        {
            MessageBox.Show("Vul de PIN in die op het scherm van de ontvangende pc wordt getoond.", "PCTransfer11",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ct = BeginOperation();
        try
        {
            string tempPackagePath = Path.Combine(Path.GetTempPath(), "PCTransfer11_te_verzenden.pctbackup");
            var builder = new PackageBuilder(_logProgress);
            var checkedFiles = GetCheckedFiles().ToList();
            var checkedApps = GetCheckedApps().ToList();
            var buildProgress = new Progress<double>(p => ((IProgress<double>)_percentProgress).Report(p * 0.5));
            await Task.Run(() => builder.BuildToZipAsync(checkedFiles, checkedApps, tempPackagePath, buildProgress, ct, _currentFileProgress), ct);

            var sendProgress = new Progress<double>(p => ((IProgress<double>)_percentProgress).Report(0.5 + p * 0.5));
            await Task.Run(() => _networkSender.SendAsync(ip, port, tempPackagePath, pin, sendProgress, _logProgress, ct, _currentFileProgress), ct);

            MessageBox.Show("Verzending voltooid.", "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log("Verzending gestopt door gebruiker.");
        }
        catch (Exception ex)
        {
            Log($"Fout tijdens verzenden: [{ex.GetType().Name}] {ex.Message}");
            MessageBox.Show($"Er ging iets mis tijdens het verzenden:\n{ex.Message}", "PCTransfer11",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private void EncryptBackupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        BackupPasswordBox.Visibility = EncryptBackupCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Kies waar de back-upmap moet komen" };
        if (dialog.ShowDialog() != true) return;

        bool encrypt = EncryptBackupCheckBox.IsChecked == true;
        string password = BackupPasswordBox.Password;
        if (encrypt && string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Vul eerst een wachtwoord in, of vink 'versleutelen' uit.", "PCTransfer11",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string backupFolder = Path.Combine(dialog.FolderName, $"PCTransfer_backup_{DateTime.Now:yyyyMMdd_HHmmss}");

        var ct = BeginOperation();
        try
        {
            var builder = new PackageBuilder(_logProgress);
            var checkedFiles = GetCheckedFiles().ToList();
            var checkedApps = GetCheckedApps().ToList();

            if (!encrypt)
            {
                await Task.Run(() => builder.BuildToDirectoryAsync(checkedFiles, checkedApps, backupFolder, _percentProgress, ct, _currentFileProgress), ct);
                MessageBox.Show(
                    $"Back-up gemaakt in:\n{backupFolder}\n\nJe kan deze map direct openen, bekijken en bewerken in Verkenner.",
                    "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                string encryptedFile = backupFolder + ".pcte";
                Log("Back-up wordt gemaakt en daarna versleuteld ...");
                // Verdeeld over twee fasen: bouwen/comprimeren (0-60%) en versleutelen (60-100%),
                // anders blijft de balk tijdens één van beide fasen op 0% staan.
                var buildProgress = new Progress<double>(p => ((IProgress<double>)_percentProgress).Report(p * 0.6));
                var encryptProgress = new Progress<double>(p => ((IProgress<double>)_percentProgress).Report(0.6 + p * 0.4));
                string plainZip = await Task.Run(() => builder.BuildToZipAsync(checkedFiles, checkedApps, backupFolder + ".zip.tmp", buildProgress, ct, _currentFileProgress), ct);
                try
                {
                    await Task.Run(() => BackupEncryption.EncryptFileAsync(plainZip, encryptedFile, password, _logProgress, ct, encryptProgress, _currentFileProgress), ct);
                }
                finally
                {
                    try { File.Delete(plainZip); } catch { /* best effort opruimen van het tijdelijke, onversleutelde bestand */ }
                }
                MessageBox.Show(
                    $"Versleutelde back-up gemaakt:\n{encryptedFile}\n\nBewaar het wachtwoord goed - zonder dat " +
                    "wachtwoord kan dit bestand nooit meer worden geopend, ook niet door Darkerst Inc.",
                    "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Back-up gestopt door gebruiker.");
            MessageBox.Show($"Back-up gestopt. Wat al gekopieerd was staat nog in:\n{backupFolder}",
                "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"Fout tijdens back-up maken: [{ex.GetType().Name}] {ex.Message}");
            MessageBox.Show($"Er ging iets mis:\n{ex.Message}", "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void ChooseEncryptedBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Kies een versleuteld PCTransfer11-back-upbestand",
            Filter = "Versleutelde back-up (*.pcte)|*.pcte|Alle bestanden (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        string tempZip = Path.Combine(Path.GetTempPath(), "PCTransfer11_decrypt_" + Guid.NewGuid().ToString("N") + ".zip");
        string tempExtractDir = Path.Combine(Path.GetTempPath(), "PCTransfer11_decrypted_" + Guid.NewGuid().ToString("N"));

        var ct = BeginOperation();
        try
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                var passwordWindow = new PasswordPromptWindow($"Wachtwoord voor '{Path.GetFileName(dialog.FileName)}':")
                {
                    Owner = this
                };
                if (passwordWindow.ShowDialog() != true)
                {
                    Log("Ontsleutelen geannuleerd door gebruiker.");
                    return;
                }

                try
                {
                    Log("Back-upbestand ontsleutelen ...");
                    await Task.Run(() => BackupEncryption.DecryptFileAsync(dialog.FileName, tempZip, passwordWindow.Password, ct, _percentProgress, _currentFileProgress), ct);
                    break; // gelukt
                }
                catch (InvalidDataException ex)
                {
                    if (attempt == 5)
                    {
                        MessageBox.Show($"Kon het bestand niet ontsleutelen:\n{ex.Message}", "PCTransfer11",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    Log($"Ontsleutelen mislukt: {ex.Message} - probeer opnieuw.");
                    continue;
                }
            }

            Log("Back-up uitpakken ...");
            await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtractDir), ct);

            var manifest = PackageRestorer.LoadManifest(tempExtractDir);
            _selectedBackupFolder = tempExtractDir;
            _selectedBackupManifest = manifest;

            _restoreItems.Clear();
            foreach (var f in manifest.Files)
                _restoreItems.Add(new RestoreSelectionItem { DisplayName = f.DisplayName, Key = f.PackagePath, IsSetting = false, IsChecked = true });
            foreach (var s in manifest.Settings)
                _restoreItems.Add(new RestoreSelectionItem { DisplayName = $"Instellingen: {s.DisplayName}", Key = s.AppId, IsSetting = true, IsChecked = true });

            RestoreItemsControl.ItemsSource = null;
            RestoreItemsControl.ItemsSource = _restoreItems;
            RestoreSelectionPanel.Visibility = Visibility.Visible;
            SelectedBackupFolderText.Text = $"Gekozen (versleutelde) back-up: {dialog.FileName} ({manifest.CreatedAtUtc:g} UTC, van {DescribeOrigin(manifest)})";
            Log($"Versleutelde back-up ontsleuteld en ingelezen: {dialog.FileName} ({_restoreItems.Count} items gevonden).");
        }
        catch (Exception ex)
        {
            RestoreSelectionPanel.Visibility = Visibility.Collapsed;
            _selectedBackupFolder = null;
            _selectedBackupManifest = null;
            MessageBox.Show($"Kon dit bestand niet als PCTransfer11-back-up inlezen:\n{ex.Message}", "PCTransfer11",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* best effort */ }
            EndOperation();
        }
    }

    // ================= TERUGZETTEN (selectief) =================

    private void ChooseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Kies een PCTransfer11-back-upmap" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var manifest = PackageRestorer.LoadManifest(dialog.FolderName);
            _selectedBackupFolder = dialog.FolderName;
            _selectedBackupManifest = manifest;

            _restoreItems.Clear();
            foreach (var f in manifest.Files)
                _restoreItems.Add(new RestoreSelectionItem { DisplayName = f.DisplayName, Key = f.PackagePath, IsSetting = false, IsChecked = true });
            foreach (var s in manifest.Settings)
                _restoreItems.Add(new RestoreSelectionItem { DisplayName = $"Instellingen: {s.DisplayName}", Key = s.AppId, IsSetting = true, IsChecked = true });

            RestoreItemsControl.ItemsSource = null;
            RestoreItemsControl.ItemsSource = _restoreItems;
            RestoreSelectionPanel.Visibility = Visibility.Visible;
            SelectedBackupFolderText.Text = $"Gekozen back-up: {dialog.FolderName} ({manifest.CreatedAtUtc:g} UTC, van {DescribeOrigin(manifest)})";
            Log($"Back-upmap ingelezen: {dialog.FolderName} ({_restoreItems.Count} items gevonden).");
        }
        catch (Exception ex)
        {
            RestoreSelectionPanel.Visibility = Visibility.Collapsed;
            _selectedBackupFolder = null;
            _selectedBackupManifest = null;
            MessageBox.Show($"Kon deze map niet als PCTransfer11-back-up inlezen:\n{ex.Message}", "PCTransfer11",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreSelectAll_Click(object sender, RoutedEventArgs e) => SetAllRestoreChecks(true);
    private void RestoreSelectNone_Click(object sender, RoutedEventArgs e) => SetAllRestoreChecks(false);

    private void SetAllRestoreChecks(bool value)
    {
        foreach (var item in _restoreItems)
            item.IsChecked = value;
        RestoreItemsControl.ItemsSource = null;
        RestoreItemsControl.ItemsSource = _restoreItems;
    }

    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBackupFolder == null || _selectedBackupManifest == null)
        {
            MessageBox.Show("Kies eerst een back-upmap.", "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var checkedFilePaths = _restoreItems.Where(i => !i.IsSetting && i.IsChecked).Select(i => i.Key).ToHashSet();
        var checkedAppIds = _restoreItems.Where(i => i.IsSetting && i.IsChecked).Select(i => i.Key).ToHashSet();

        if (checkedFilePaths.Count == 0 && checkedAppIds.Count == 0)
        {
            MessageBox.Show("Vink minstens één item aan om terug te zetten.", "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            "Bestaande bestanden met dezelfde naam worden overschreven. Doorgaan?",
            "PCTransfer11", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var ct = BeginOperation();
        try
        {
            var restorer = new PackageRestorer(_logProgress);
            await Task.Run(() => restorer.RestoreFromFolderAsync(
                _selectedBackupFolder, _selectedBackupManifest,
                checkedFilePaths, checkedAppIds,
                overwriteExisting: true, _percentProgress, ct, _currentFileProgress), ct);
            MessageBox.Show("Terugzetten voltooid.", "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log("Terugzetten gestopt door gebruiker.");
        }
        catch (Exception ex)
        {
            Log($"Fout tijdens terugzetten: [{ex.GetType().Name}] {ex.Message}");
            MessageBox.Show($"Er ging iets mis tijdens het terugzetten:\n{ex.Message}", "PCTransfer11",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private IEnumerable<FileSelectionItem> GetCheckedFiles() => _fileItems.Where(f => f.IsChecked && f.Exists);
    private IEnumerable<AppProfile> GetCheckedApps() => _appProfiles.Where(a => a.IsChecked && a.IsAvailable);

    /// <summary>
    /// Start een nieuwe annuleerbare operatie: maakt een verse CancellationTokenSource,
    /// schakelt de Stop-knop in en reset de voortgangsbalk. Geef het geretourneerde
    /// token mee aan de service-aanroep in plaats van CancellationToken.None.
    /// </summary>
    private CancellationToken BeginOperation(bool switchToProgressTab = true)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        SetBusy(true);
        if (switchToProgressTab)
            MainTabControl.SelectedIndex = 2; // Tab 3: Voortgang - meteen zichtbaar zodra een actie start
        return _operationCts.Token;
    }

    private void EndOperation()
    {
        SetBusy(false);
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts == null || _operationCts.IsCancellationRequested) return;
        Log("Bezig met stoppen ... (kan even duren tot het huidige bestand klaar is)");
        StopButton.IsEnabled = false;
        _operationCts.Cancel();
    }

    private void SetBusy(bool busy)
    {
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
        StopButton.IsEnabled = busy;
        if (busy)
        {
            TransferProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";
            CurrentFileText.Text = "";
            OpenExtractedFolderButton.Visibility = Visibility.Collapsed;
        }
    }
}
