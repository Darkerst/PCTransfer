using System.Windows;
using System.Windows.Threading;
using PCTransfer11.Services;

namespace PCTransfer11;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Als dit een kortstondige, onzichtbare "als administrator"-herlancering
        // is (voor netwerkadapter-/Wifi-instellingen, zie ElevatedNetworkHelper),
        // dan wordt hier alleen dat ene commando uitgevoerd en meteen afgesloten -
        // er wordt nooit een venster getoond voor deze modus.
        if (ElevatedNetworkHelper.TryHandleElevatedArgs(e.Args))
        {
            Shutdown(0);
            return;
        }

        // Globale vangnetten zo vroeg mogelijk registreren, VOORDAT het
        // hoofdvenster wordt aangemaakt - dit was eerder het probleem: als
        // MainWindow (via StartupUri) een fout gooit tijdens het opstarten
        // (bv. een XAML/binding-fout), sloot de app zonder enige melding.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            ShowCrashWindowAndExit(ex, "Opstarten van het hoofdvenster");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // voorkom de standaard, meldingsloze crash
        ShowCrashWindowAndExit(e.Exception, "Onverwachte fout op de UI-thread");
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowCrashWindowAndExit(ex, "Onverwachte fatale fout (achtergrondthread)");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Dit zijn fouten in "vergeten" achtergrondtaken - niet altijd fataal,
        // dus alleen loggen naar schijf en niet de app onderuit halen.
        e.SetObserved();
        string report = CrashReporter.BuildReport(e.Exception, "Onbeheerde taakfout (niet-fataal, op de achtergrond afgevangen)");
        CrashReporter.TrySaveReport(report);
    }

    private void ShowCrashWindowAndExit(Exception ex, string context)
    {
        try
        {
            string report = CrashReporter.BuildReport(ex, context);
            string? savedPath = CrashReporter.TrySaveReport(report);

            var crashWindow = new CrashWindow("PCTransfer11 - Onverwachte fout", report, savedPath);
            crashWindow.ShowDialog();
        }
        catch
        {
            // Als zelfs het crashvenster niet kan openen, toon dan op zijn minst
            // een kale MessageBox zodat de gebruiker niet met niets achterblijft.
            MessageBox.Show($"PCTransfer11 is gecrasht:\n\n{ex}", "PCTransfer11 - Fout",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Shutdown(-1);
        }
    }
}
