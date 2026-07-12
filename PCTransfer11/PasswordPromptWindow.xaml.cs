using System.Windows;
using System.Windows.Input;

namespace PCTransfer11;

public partial class PasswordPromptWindow : Window
{
    public string Password { get; private set; } = "";

    public PasswordPromptWindow(string prompt)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>Toont een foutmelding (bv. "verkeerd wachtwoord") zonder het venster te sluiten.</summary>
    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PasswordInput.SelectAll();
        PasswordInput.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordInput.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Ok_Click(sender, e);
    }
}
