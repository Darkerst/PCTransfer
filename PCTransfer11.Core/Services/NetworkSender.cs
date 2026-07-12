using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCTransfer11.Models;

namespace PCTransfer11.Services;

/// <summary>
/// De zendende kant van een netwerkoverdracht: vindt ontvangende pc's op het
/// lokale netwerk via UDP-broadcast, en stuurt daarna het pakket via TCP.
/// </summary>
public sealed class NetworkSender
{
    private const string DiscoveryRequestMagic = "PCTRANSFER11_DISCOVER";
    private const string DiscoveryReplyMagic = "PCTRANSFER11_HERE";

    /// <summary>
    /// Stuurt een UDP-broadcast het lokale netwerk op en verzamelt gedurende
    /// <paramref name="timeoutMs"/> milliseconden alle reacties van draaiende
    /// PCTransfer11-ontvangers.
    /// </summary>
    public static async Task<List<DiscoveredReceiver>> DiscoverAsync(int timeoutMs, CancellationToken ct)
    {
        var found = new List<DiscoveredReceiver>();
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;

        byte[] requestBytes = Encoding.UTF8.GetBytes(DiscoveryRequestMagic);
        await udp.SendAsync(requestBytes, requestBytes.Length,
            new IPEndPoint(IPAddress.Broadcast, NetworkReceiver.DiscoveryPort));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(timeoutCts.Token);
                string text = Encoding.UTF8.GetString(result.Buffer);
                string[] parts = text.Split('|');
                if (parts.Length == 3 && parts[0] == DiscoveryReplyMagic &&
                    int.TryParse(parts[2], out int port))
                {
                    string ip = result.RemoteEndPoint.Address.ToString();
                    if (!found.Any(f => f.IpAddress == ip))
                        found.Add(new DiscoveredReceiver(ip, parts[1], port));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // timeout bereikt - normale manier om de zoektocht af te ronden
        }

        return found;
    }

    /// <summary>
    /// Verstuurt het pakketbestand naar de opgegeven ontvanger. <paramref name="pin"/>
    /// moet overeenkomen met de PIN die op de ontvangende pc wordt getoond -
    /// zonder overeenkomende PIN weigert de ontvanger de overdracht, en de
    /// hele overdracht wordt met AES-256 versleuteld met een sleutel afgeleid
    /// van die PIN.
    /// </summary>
    public async Task SendAsync(string ipAddress, int tcpPort, string filePath, string pin, IProgress<double> progress,
        IProgress<string> log, CancellationToken ct, IProgress<string>? currentFileProgress = null)
    {
        currentFileProgress?.Report($"Verzenden: {Path.GetFileName(filePath)}");

        using var client = new TcpClient();
        log.Report($"Verbinden met {ipAddress}:{tcpPort} ...");
        await client.ConnectAsync(ipAddress, tcpPort, ct);
        log.Report("Verbonden. PIN controleren ...");

        using var networkStream = client.GetStream();

        byte[] key = NetworkCrypto.DeriveKey(pin);
        byte[] iv = new byte[16];
        byte[] nonce = new byte[16];
        await NetworkCrypto.ReadExactAsync(networkStream, iv, ct);
        await NetworkCrypto.ReadExactAsync(networkStream, nonce, ct);

        byte[] hmacResponse;
        using (var hmac = new HMACSHA256(key))
            hmacResponse = hmac.ComputeHash(nonce);
        await networkStream.WriteAsync(hmacResponse, ct);

        byte[] okByte = new byte[1];
        await NetworkCrypto.ReadExactAsync(networkStream, okByte, ct);
        if (okByte[0] != 1)
            throw new InvalidOperationException(
                "Verkeerde PIN - de ontvangende pc heeft de overdracht geweigerd. Controleer of je de PIN " +
                "overneemt die op het scherm van de ontvangende pc staat.");

        log.Report("PIN klopt. Overdracht gestart (versleuteld) ...");

        // Toestelnaam onversleuteld meesturen (net als iv/nonce hierboven), zodat de
        // ontvangende kant de overdracht een herkenbare naam kan geven i.p.v.
        // "Onbekend apparaat". Compatibel met de Android-kant, die ditzelfde
        // lengte-geprefixte veld verstuurt/leest.
        byte[] nameBytes = Encoding.UTF8.GetBytes(Environment.MachineName);
        if (nameBytes.Length > 255) nameBytes = nameBytes[..255];
        await networkStream.WriteAsync(BitConverter.GetBytes((ushort)nameBytes.Length), ct);
        await networkStream.WriteAsync(nameBytes, ct);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        await using var fileStream = File.OpenRead(filePath);
        long totalBytes = fileStream.Length;

        await using (var cryptoStream = new CryptoStream(networkStream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            byte[] lengthBytes = BitConverter.GetBytes(totalBytes);
            await cryptoStream.WriteAsync(lengthBytes, ct);

            byte[] buffer = new byte[81920];
            long sent = 0;
            int read;
            while ((read = await fileStream.ReadAsync(buffer, ct)) > 0)
            {
                await cryptoStream.WriteAsync(buffer.AsMemory(0, read), ct);
                sent += read;
                progress.Report(totalBytes == 0 ? 1.0 : (double)sent / totalBytes);
            }

            await cryptoStream.FlushFinalBlockAsync(ct);
        }

        log.Report("Verzending voltooid.");
    }
}
