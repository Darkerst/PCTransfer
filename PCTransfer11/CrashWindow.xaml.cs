using System.Windows;

namespace PCTransfer11;

/// <summary>
/// Toont een crash-rapport dat de gebruiker kan kopiëren/doorsturen.
/// Wordt aangeroepen door de globale exception-handlers in App.xaml.cs.
/// Dit venster is bewust simpel gehouden (geen bindings, geen afhankelijkheid
/// van App.xaml-resources) zodat het ook nog opent als de crash zelf door
/// resource- of opstartproblemen kwam.
/// </summary>
public partial class CrashWindow : Window
{
    private readonly string _crashText;

    public CrashWindow(string title, string crashText, string? savedFilePath)
    {
        InitializeComponent();

        _crashText = crashText;
        if (!string.IsNullOrEmpty(title))
            Title = title;

        CrashDetailsTextBox.Text = crashText;
        SavedPathText.Text = savedFilePath != null
            ? $"Opgeslagen als: {savedFilePath}"
            : string.Empty;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        // Clipboard.SetText kan af en toe mislukken doordat een ander proces
        // het klembord even vasthoudt (OpenClipboard-race, komt vaker voor op
        // Windows dan je zou willen) - daarom een paar keer opnieuw proberen.
        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(_crashText);
                lastError = null;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                System.Threading.Thread.Sleep(80);
            }
        }

        if (lastError == null)
        {
            CopyButton.Content = "Gekopieerd!";
        }
        else
        {
            MessageBox.Show(this, $"Kopiëren naar klembord is niet gelukt:\n{lastError.Message}\n\n" +
                                   "Selecteer de tekst hierboven handmatig en gebruik Ctrl+C.",
                "PCTransfer11", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
