using System;
using System.IO;
using System.Text.Json;

namespace PCTransfer11.Services;

/// <summary>
/// Registreert wanneer de laatste back-up was en berekent of de gebruiker
/// een herinnering moet krijgen (standaard: elke 30 dagen).
/// </summary>
public static class BackupReminder
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCTransfer11", "reminder.json");

    public sealed class ReminderSettings
    {
        public DateTime LastBackupUtc { get; set; } = DateTime.MinValue;
        public int ReminderIntervalDays { get; set; } = 30;
        public bool Enabled { get; set; } = true;
    }

    public static ReminderSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<ReminderSettings>(File.ReadAllText(SettingsPath)) ?? new ReminderSettings();
        }
        catch { }
        return new ReminderSettings();
    }

    public static void Save(ReminderSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void RecordBackupMade()
    {
        var s = Load();
        s.LastBackupUtc = DateTime.UtcNow;
        Save(s);
    }

    public static bool ShouldRemind(out int daysSinceLast)
    {
        var s = Load();
        if (!s.Enabled) { daysSinceLast = 0; return false; }
        if (s.LastBackupUtc == DateTime.MinValue) { daysSinceLast = -1; return true; }
        daysSinceLast = (int)(DateTime.UtcNow - s.LastBackupUtc).TotalDays;
        return daysSinceLast >= s.ReminderIntervalDays;
    }
}
