using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PCTransfer11.Services;

/// <summary>
/// Versleutelt/ontsleutelt een back-upbestand (de gezipte back-up) met een
/// door de gebruiker gekozen wachtwoord. Gebruikt alleen de ingebouwde
/// .NET-cryptografie (AES-256-CBC + PBKDF2 voor de sleutelafleiding) - geen
/// externe NuGet-packages nodig, zodat de portable .exe zelfstandig blijft.
///
/// Bestandsformaat (simpel, eigen "PCTE1"-header):
///   4 bytes  magic "PCTE"
///   1 byte   versie (1)
///   16 bytes salt (voor PBKDF2)
///   16 bytes IV (voor AES-CBC)
///   ... AES-256-CBC-versleutelde inhoud van het originele bestand ...
///
/// Waarom dit nuttig is: een back-up bevat soms Wifi-wachtwoorden in platte
/// tekst (nodig om ze te kunnen herstellen) - als die back-up op een simpele
/// USB-stick staat, is een wachtwoord op het bestand zelf een simpele
/// extra beveiligingslaag voor wie dat wil.
/// </summary>
public static class BackupEncryption
{
    private static readonly byte[] Magic = { (byte)'P', (byte)'C', (byte)'T', (byte)'E' };
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int Pbkdf2Iterations = 200_000;

    public static async Task EncryptFileAsync(string inputPath, string outputPath, string password, IProgress<string>? log, CancellationToken ct, IProgress<double>? percentProgress = null, IProgress<string>? currentFileProgress = null)
    {
        currentFileProgress?.Report($"Versleutelen: {Path.GetFileName(inputPath)}");

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] iv = RandomNumberGenerator.GetBytes(IvSize);
        byte[] key = DeriveKey(password, salt);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);

        await output.WriteAsync(Magic, ct);
        output.WriteByte(Version);
        await output.WriteAsync(salt, ct);
        await output.WriteAsync(iv, ct);

        long totalBytes = Math.Max(1, input.Length);
        long processed = 0;
        var buffer = new byte[1024 * 1024];

        await using (var cryptoStream = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await cryptoStream.WriteAsync(buffer.AsMemory(0, read), ct);
                processed += read;
                percentProgress?.Report(Math.Min(1.0, (double)processed / totalBytes));
            }
            await cryptoStream.FlushFinalBlockAsync(ct);
        }

        log?.Report("Back-up versleuteld met wachtwoord.");
    }

    /// <summary>
    /// Ontsleutelt naar <paramref name="outputPath"/>. Gooit een
    /// <see cref="CryptographicException"/>-achtige fout als het wachtwoord
    /// fout is (herkenbaar aan een "verkeerd wachtwoord"-bericht).
    /// </summary>
    public static async Task DecryptFileAsync(string inputPath, string outputPath, string password, CancellationToken ct, IProgress<double>? percentProgress = null, IProgress<string>? currentFileProgress = null)
    {
        currentFileProgress?.Report($"Ontsleutelen: {Path.GetFileName(inputPath)}");

        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);

        var magicBuf = new byte[4];
        if (await input.ReadAsync(magicBuf, ct) != 4 || magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1]
            || magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            throw new InvalidDataException("Dit is geen door PCTransfer11 versleuteld back-upbestand.");

        int version = input.ReadByte();
        if (version != Version)
            throw new InvalidDataException($"Onbekende versie van het versleutelingsformaat ({version}).");

        var salt = new byte[SaltSize];
        var iv = new byte[IvSize];
        await input.ReadExactlyAsync(salt, ct);
        await input.ReadExactlyAsync(iv, ct);

        byte[] key = DeriveKey(password, salt);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        long totalBytes = Math.Max(1, input.Length - input.Position);
        long processed = 0;
        var buffer = new byte[1024 * 1024];

        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        try
        {
            await using var cryptoStream = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
            int read;
            while ((read = await cryptoStream.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                processed += read;
                percentProgress?.Report(Math.Min(1.0, (double)processed / totalBytes));
            }
        }
        catch (CryptographicException)
        {
            // Bijna altijd een verkeerd wachtwoord (PKCS7-padding klopt dan niet meer).
            throw new InvalidDataException("Verkeerd wachtwoord (of het bestand is beschadigd).");
        }
    }

    /// <summary>Snelle check of een bestand met de eigen "PCTE"-header begint, zonder het al te ontsleutelen.</summary>
    public static bool LooksEncrypted(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[4];
            return fs.Read(buf, 0, 4) == 4 && buf[0] == Magic[0] && buf[1] == Magic[1] && buf[2] == Magic[2] && buf[3] == Magic[3];
        }
        catch { return false; }
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password.Trim(), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
}
