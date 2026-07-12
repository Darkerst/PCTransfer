using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCTransfer11.Services;

/// <summary>
/// Gedeelde hulpfuncties voor het beveiligen van de rechtstreekse pc-naar-pc-
/// overdracht met een PIN: sleutelafleiding, en het lezen van een exact
/// aantal bytes (nodig voor de handshake-header, die geen los "gedeeltelijk"
/// resultaat kan verwerken).
///
/// Zonder dit zou elke andere pc op hetzelfde (Wifi-)netwerk in principe
/// kunnen meeluisteren of zich voordoen als ontvanger - de PIN zorgt dat
/// alleen de kant die de PIN kent, de gegevens kan lezen.
/// </summary>
internal static class NetworkCrypto
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("PCTransfer11-Network-Pairing-Salt-v1");
    private const int Pbkdf2Iterations = 100_000;

    /// <summary>
    /// Overdrachtsprotocol-versie (1 byte, direct na de PIN-check verstuurd/
    /// gelezen). Verhoog dit getal ALTIJD samen aan beide kanten (Windows én
    /// Android) zodra het bytformaat van de overdracht verandert (bv. een
    /// nieuw veld) - zo krijg je bij een mismatch een duidelijke foutmelding
    /// in plaats van een overdracht die stilletjes verkeerd wordt gelezen.
    /// </summary>
    public const byte ProtocolVersion = 2;

    public static byte[] DeriveKey(string pin) =>
        Rfc2898DeriveBytes.Pbkdf2(pin.Trim(), Salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Genereert een willekeurige, makkelijk over te typen 6-cijferige PIN.</summary>
    public static string GeneratePin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new IOException("Verbinding werd onverwacht verbroken.");
            offset += read;
        }
    }
}
