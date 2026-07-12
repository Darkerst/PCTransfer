using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PCTransfer11.Services;

/// <summary>
/// Controleert na een back-up of het resulterende zipbestand geldig en
/// leesbaar is — zodat je zekerheid hebt vóórdat je de originele bestanden
/// aanraakt.
/// </summary>
public static class BackupVerifier
{
    public static async Task<string> ComputeHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        byte[] hash = await Task.Run(() => sha.ComputeHash(stream), ct);
        return Convert.ToHexString(hash);
    }

    public static async Task<(bool Success, string Message)> VerifyZipAsync(
        string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(zipPath))
                return (false, $"Back-upbestand niet gevonden: {zipPath}");

            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries;
            progress?.Report(0.1);

            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry == null)
                return (false, "manifest.json ontbreekt - het bestand is mogelijk corrupt.");

            using var reader = new StreamReader(manifestEntry.Open());
            string manifestJson = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(manifestJson))
                return (false, "manifest.json is leeg - het bestand is mogelijk corrupt.");
            progress?.Report(0.3);

            int checked_ = 0, total = entries.Count;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Length == 0) continue;
                try
                {
                    await using var stream = entry.Open();
                    byte[] buf = new byte[4096];
                    await stream.ReadAsync(buf, ct);
                }
                catch (InvalidDataException ex)
                {
                    return (false, $"Corrupt bestand ({entry.FullName}): {ex.Message}");
                }
                checked_++;
                progress?.Report(0.3 + 0.7 * checked_ / Math.Max(1, total));
            }

            progress?.Report(1.0);
            return (true, $"Back-up geverifieerd — {total} entries gecontroleerd, alles in orde.");
        }
        catch (InvalidDataException ex)
        {
            return (false, $"Geen geldig zipbestand: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Verificatie mislukt: {ex.Message}");
        }
    }
}
