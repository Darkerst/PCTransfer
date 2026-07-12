using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PCTransfer11.Services;
using Xunit;

namespace PCTransfer11.Tests;

public class BackupEncryptionTests
{
    [Fact]
    public async Task EncryptThenDecrypt_WithCorrectPassword_RestoresOriginalContent()
    {
        string plainPath = Path.GetTempFileName();
        string encryptedPath = plainPath + ".pcte";
        string decryptedPath = plainPath + ".decrypted";
        byte[] originalContent = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        try
        {
            await File.WriteAllBytesAsync(plainPath, originalContent);

            await BackupEncryption.EncryptFileAsync(plainPath, encryptedPath, "correct-horse-battery-staple", log: null, CancellationToken.None);
            Assert.True(BackupEncryption.LooksEncrypted(encryptedPath));

            await BackupEncryption.DecryptFileAsync(encryptedPath, decryptedPath, "correct-horse-battery-staple", CancellationToken.None);

            byte[] decryptedContent = await File.ReadAllBytesAsync(decryptedPath);
            Assert.Equal(originalContent, decryptedContent);
        }
        finally
        {
            foreach (string f in new[] { plainPath, encryptedPath, decryptedPath })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Decrypt_WithWrongPassword_ThrowsInvalidDataException()
    {
        string plainPath = Path.GetTempFileName();
        string encryptedPath = plainPath + ".pcte";
        string decryptedPath = plainPath + ".decrypted";

        try
        {
            await File.WriteAllTextAsync(plainPath, "geheime inhoud");
            await BackupEncryption.EncryptFileAsync(plainPath, encryptedPath, "juist-wachtwoord", log: null, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BackupEncryption.DecryptFileAsync(encryptedPath, decryptedPath, "fout-wachtwoord", CancellationToken.None));
        }
        finally
        {
            foreach (string f in new[] { plainPath, encryptedPath, decryptedPath })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void LooksEncrypted_ReturnsFalse_ForOrdinaryFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "gewoon een normaal bestand");
            Assert.False(BackupEncryption.LooksEncrypted(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
