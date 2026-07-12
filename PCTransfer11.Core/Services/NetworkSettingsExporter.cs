using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PCTransfer11.Services;

/// <summary>
/// Exporteert en importeert bekende Wifi-netwerken (SSID + wachtwoord) met
/// het ingebouwde Windows-hulpprogramma netsh - geen extra installatie nodig.
///
/// Let op: het meenemen van het wachtwoord in klare tekst (key=clear) vereist
/// op sommige systemen adminrechten. Als dat niet lukt, valt de export terug
/// op profielen ZONDER wachtwoord (SSID/beveiligingstype gaan dan nog wel
/// mee, alleen moet het wachtwoord op de nieuwe pc handmatig worden
/// ingevuld bij de eerste keer verbinden).
/// </summary>
public static class NetworkSettingsExporter
{
    public static async Task<bool> ExportWifiProfilesAsync(string destDir, CancellationToken ct, IProgress<string> log)
    {
        Directory.CreateDirectory(destDir);

        bool anyExported = await TryExportAsync(destDir, withKey: true, ct, log);
        if (!anyExported)
        {
            log.Report("Wifi-wachtwoorden in klare tekst exporteren lukte niet (vereist mogelijk adminrechten) - " +
                       "probeer opnieuw zonder wachtwoorden (alleen netwerknaam/beveiligingstype) ...");
            anyExported = await TryExportAsync(destDir, withKey: false, ct, log);
        }

        return anyExported && Directory.GetFiles(destDir, "*.xml").Length > 0;
    }

    private static async Task<bool> TryExportAsync(string destDir, bool withKey, CancellationToken ct, IProgress<string> log)
    {
        try
        {
            string args = withKey
                ? $"wlan export profile folder=\"{destDir}\" key=clear"
                : $"wlan export profile folder=\"{destDir}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            log.Report($"Kon Wifi-profielen niet exporteren: {ex.Message}");
            return false;
        }
    }

    public static async Task ImportWifiProfilesAsync(string sourceDir, CancellationToken ct, IProgress<string> log)
    {
        if (!Directory.Exists(sourceDir)) return;

        foreach (string xmlFile in Directory.GetFiles(sourceDir, "*.xml"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"wlan add profile filename=\"{xmlFile}\" user=current",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(psi);
                if (process == null) continue;
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0)
                    log.Report($"Waarschuwing: Wifi-profiel '{Path.GetFileNameWithoutExtension(xmlFile)}' kon mogelijk niet worden toegevoegd.");
            }
            catch (Exception ex)
            {
                log.Report($"Kon Wifi-profiel '{Path.GetFileName(xmlFile)}' niet terugzetten: {ex.Message}");
            }
        }
    }
}
