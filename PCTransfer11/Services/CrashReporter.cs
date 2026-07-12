using System.IO;
using System.Reflection;
using System.Text;

namespace PCTransfer11.Services;

/// <summary>
/// Zet een Exception om in een leesbaar, kopieerbaar rapport en slaat het
/// ook op schijf op, zodat een crash nooit spoorloos verdwijnt - ook niet
/// als de gebruiker het CrashWindow per ongeluk wegklikt.
/// </summary>
public static class CrashReporter
{
    public static string BuildReport(Exception ex, string context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PCTransfer11 - foutrapport");
        sb.AppendLine($"Tijdstip:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Context:         {context}");
        sb.AppendLine($"App-versie:      {Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"OS:              {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        sb.AppendLine($".NET:            {Environment.Version}");
        sb.AppendLine($"Machine:         {Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine("---- Uitzondering ----");
        AppendException(sb, ex, 0);
        return sb.ToString();
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        string indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}Type:    {ex.GetType().FullName}");
        sb.AppendLine($"{indent}Bericht: {ex.Message}");
        sb.AppendLine($"{indent}Stack trace:");
        sb.AppendLine(ex.StackTrace ?? $"{indent}  (geen stack trace beschikbaar)");

        if (ex is AggregateException agg)
        {
            int i = 1;
            foreach (var inner in agg.InnerExceptions)
            {
                sb.AppendLine();
                sb.AppendLine($"{indent}-- AggregateException, binnenste fout {i} --");
                AppendException(sb, inner, depth + 1);
                i++;
            }
        }
        else if (ex.InnerException != null)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}-- Binnenste fout (InnerException) --");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }

    /// <summary>
    /// Slaat het rapport op in %LOCALAPPDATA%\PCTransfer11\crashes en geeft het
    /// volledige pad terug, of null als opslaan zelf ook mislukte (dan is er
    /// tenminste nog het venster met de kopieerbare tekst).
    /// </summary>
    public static string? TrySaveReport(string report)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCTransfer11", "crashes");
            Directory.CreateDirectory(folder);

            string file = Path.Combine(folder, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(file, report);
            return file;
        }
        catch
        {
            return null;
        }
    }
}
