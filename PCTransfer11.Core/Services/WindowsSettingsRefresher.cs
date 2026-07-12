using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCTransfer11.Services;

/// <summary>
/// "reg import" past een registerwaarde aan, maar Windows Verkenner/de
/// grafische schil léést die waarden meestal maar één keer in (bij opstarten
/// of aanmelden) en houdt ze daarna in het geheugen. Alleen het register
/// bijwerken verandert dus zichtbaar niets - de bureaubladachtergrond
/// bijvoorbeeld wordt pas echt opnieuw getekend na een expliciete
/// SystemParametersInfo-aanroep (of een volledige uit/aanmeldbeurt).
///
/// Deze klasse doet, ná het terugzetten van de Windows-instellingen, een
/// best-effort poging om dat direct te forceren zonder dat de gebruiker
/// hoeft uit/in te loggen: de achtergrond opnieuw laten tekenen en een
/// systeembrede "instellingen zijn gewijzigd"-melding uitzenden zodat
/// Verkenner en andere programma's die oppikken. Voor sommige onderdelen
/// (met name Verkenner-weergave-instellingen en sommige regio-instellingen)
/// blijft een keer afmelden/aanmelden voor 100% zekerheid toch het betrouwbaarst.
/// </summary>
internal static class WindowsSettingsRefresher
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDWININICHANGE = 0x02;
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string? pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr result);

    /// <summary>
    /// Forceert waar mogelijk dat zonder afmelden zichtbaar wordt: de
    /// bureaubladachtergrond opnieuw tekenen, en een systeembrede melding
    /// uitzenden dat omgevings-/regio-instellingen zijn gewijzigd.
    /// </summary>
    public static void TryApplyImmediately(IProgress<string> log)
    {
        bool anyApplied = false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? wallpaper = key?.GetValue("Wallpaper") as string;
            if (!string.IsNullOrWhiteSpace(wallpaper) && File.Exists(wallpaper))
            {
                if (SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, wallpaper, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE))
                    anyApplied = true;
            }
        }
        catch { /* best effort - puur cosmetisch, mag de terugzetactie nooit laten mislukken */ }

        try
        {
            // Vertelt draaiende programma's (o.a. Verkenner) dat omgevings- en
            // regio-instellingen zijn gewijzigd, zonder dat ze herstart hoeven te worden.
            SendMessageTimeoutW((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 3000, out _);
            SendMessageTimeoutW((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Policy", SMTO_ABORTIFHUNG, 3000, out _);
            SendMessageTimeoutW((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "intl", SMTO_ABORTIFHUNG, 3000, out _);
            anyApplied = true;
        }
        catch { /* best effort */ }

        log.Report(anyApplied
            ? "Bureaubladachtergrond en omgevingsinstellingen direct ververst (zonder opnieuw aan te melden)."
            : "Kon de instellingen niet direct verversen.");
        log.Report("Let op: sommige onderdelen (Verkenner-/taakbalkweergave, volledige regio-omschakeling) " +
                    "worden pas zeker zichtbaar na één keer afmelden en weer aanmelden (of een herstart).");
    }
}
