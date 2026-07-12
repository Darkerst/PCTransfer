using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCTransfer11.Services;

/// <summary>
/// Bijhoudt welke bestanden al in een vorige back-up zaten op basis van
/// pad + tijdstempel + grootte, zodat bij een differentiële back-up alleen
/// gewijzigde bestanden worden meegenomen.
/// </summary>
public static class BackupHistory
{
    private const string IndexFileName = "backup_index.json";

    public sealed class FileRecord
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
    }

    public sealed class BackupIndex
    {
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string CreatedByMachine { get; set; } = Environment.MachineName;
        public List<FileRecord> Files { get; set; } = new();
    }

    public static BackupIndex? LoadLatestIndex(string baseDirectory)
    {
        try
        {
            var candidates = Directory.GetFiles(baseDirectory, IndexFileName, SearchOption.AllDirectories)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .ToList();
            if (candidates.Count == 0) return null;
            string json = File.ReadAllText(candidates[0]);
            return JsonSerializer.Deserialize<BackupIndex>(json);
        }
        catch { return null; }
    }

    public static bool IsChanged(string filePath, BackupIndex? previousIndex)
    {
        if (previousIndex == null) return true;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return true;
            var prev = previousIndex.Files.FirstOrDefault(r =>
                string.Equals(r.Path, filePath, StringComparison.OrdinalIgnoreCase));
            if (prev == null) return true;
            return info.Length != prev.Size || info.LastWriteTimeUtc.Ticks != prev.LastWriteUtcTicks;
        }
        catch { return true; }
    }

    public static async Task SaveIndex(string backupDirectory, IEnumerable<string> copiedFiles, CancellationToken ct)
    {
        var index = new BackupIndex();
        foreach (string file in copiedFiles)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists)
                    index.Files.Add(new FileRecord { Path = file, Size = info.Length, LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks });
            }
            catch { }
        }
        string indexPath = Path.Combine(backupDirectory, IndexFileName);
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(index), ct);
    }
}
