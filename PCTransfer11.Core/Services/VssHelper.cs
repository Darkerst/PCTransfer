using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PCTransfer11.Services;

/// <summary>
/// Wrapper rond Windows Volume Shadow Copy Service (VSS). Maakt een
/// tijdelijke snapshot van een schijfvolume zodat bestanden gekopieerd
/// kunnen worden ook als ze op slot staan (bv. Edge/Chrome databases,
/// Outlook .ost-bestanden). Dit is dezelfde techniek die Acronis, Veeam
/// en alle andere serieuze back-uptools gebruiken.
///
/// Als VSS niet beschikbaar is valt de code gewoon terug op het
/// normale directe-kopie-gedrag.
/// </summary>
public sealed class VssSnapshot : IDisposable
{
    private readonly string _originalVolume;
    private string? _shadowPath;
    private readonly IProgress<string> _log;

    public VssSnapshot(string volume, IProgress<string> log)
    {
        _originalVolume = volume.TrimEnd('\\', '/') + "\\";
        _log = log;
    }

    public bool TryCreateSnapshot()
    {
        try
        {
            string volume = _originalVolume;
            string psCommand =
                $"(Get-WmiObject -List Win32_ShadowCopy).Create('{volume}', 'ClientAccessible') | Out-Null; " +
                $"(Get-WmiObject Win32_ShadowCopy | Where-Object {{ $_.VolumeName -eq '{volume}' }} | " +
                $"Sort-Object InstallDate | Select-Object -Last 1).DeviceObject";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{psCommand}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(30000);

            if (!string.IsNullOrEmpty(output) && output.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            {
                _shadowPath = output;
                _log.Report($"VSS snapshot aangemaakt voor {_originalVolume}: {_shadowPath}");
                return true;
            }
            _log.Report($"VSS snapshot mislukt voor {_originalVolume} - bestanden worden direct gekopieerd.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Report($"VSS niet beschikbaar: {ex.Message} - bestanden worden direct gekopieerd.");
            return false;
        }
    }

    public string TranslatePath(string originalPath)
    {
        if (_shadowPath == null) return originalPath;
        string fullOriginal = Path.GetFullPath(originalPath);
        string fullVolume = Path.GetFullPath(_originalVolume);
        if (fullOriginal.StartsWith(fullVolume, StringComparison.OrdinalIgnoreCase))
        {
            string relative = fullOriginal.Substring(fullVolume.Length);
            return Path.Combine(_shadowPath, relative);
        }
        return originalPath;
    }

    public void Dispose()
    {
        if (_shadowPath == null) return;
        try
        {
            string psCleanup =
                $"$sc = Get-WmiObject Win32_ShadowCopy | Where-Object {{ $_.DeviceObject -eq '{_shadowPath}' }}; " +
                "if ($sc) { $sc.Delete() }";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{psCleanup}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(15000);
            _log.Report($"VSS snapshot opgeruimd.");
        }
        catch (Exception ex)
        {
            _log.Report($"VSS snapshot opruimen mislukt: {ex.Message}");
        }
        _shadowPath = null;
    }
}

public sealed class VssSessionManager : IDisposable
{
    private readonly Dictionary<string, VssSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly IProgress<string> _log;

    public VssSessionManager(IProgress<string> log) => _log = log;

    public string TranslateToSnapshotPath(string originalPath)
    {
        string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(originalPath));
        if (volumeRoot == null) return originalPath;
        if (!_snapshots.TryGetValue(volumeRoot, out var snapshot))
        {
            snapshot = new VssSnapshot(volumeRoot, _log);
            snapshot.TryCreateSnapshot();
            _snapshots[volumeRoot] = snapshot;
        }
        return snapshot.TranslatePath(originalPath);
    }

    public void Dispose()
    {
        foreach (var snap in _snapshots.Values)
            snap.Dispose();
        _snapshots.Clear();
    }
}
