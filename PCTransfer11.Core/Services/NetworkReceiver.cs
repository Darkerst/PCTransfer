using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCTransfer11.Services;

/// <summary>
/// De ontvangende kant van een netwerkoverdracht. Luistert op een TCP-poort
/// voor het binnenkomende pakket, en beantwoordt tegelijk UDP-discovery-
/// broadcasts van zendende pc's zodat die deze machine automatisch kunnen
/// vinden op het lokale netwerk.
/// </summary>
public sealed class NetworkReceiver
{
    public const int DefaultTcpPort = 51715;
    public const int DiscoveryPort = 51716;
    private const string DiscoveryRequestMagic = "PCTRANSFER11_DISCOVER";
    private const string DiscoveryReplyMagic = "PCTRANSFER11_HERE";

    private readonly IProgress<string> _log;

    public NetworkReceiver(IProgress<string> log)
    {
        _log = log;
    }

    /// <summary>
    /// Draait op de achtergrond en beantwoordt discoveryverzoeken totdat
    /// <paramref name="ct"/> wordt geannuleerd. Aanroepen als "fire and forget"
    /// task zodra de gebruiker naar het ontvangst-tabblad gaat.
    /// </summary>
    public async Task RunDiscoveryResponderAsync(int tcpPort, CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                string text = Encoding.UTF8.GetString(result.Buffer);
                if (text != DiscoveryRequestMagic) continue;

                string reply = $"{DiscoveryReplyMagic}|{Environment.MachineName}|{tcpPort}";
                byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                await udp.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);
            }
        }
        catch (Exception ex)
        {
            _log.Report($"Discovery-service gestopt: {ex.Message}");
        }
    }

    /// <summary>
    /// Wacht op één inkomende verbinding, ontvangt het pakket en slaat het op
    /// als <paramref name="saveAsPath"/>. Retourneert de toestelnaam die de
    /// verzendende kant heeft meegestuurd (bv. voor gebruik als voorgestelde
    /// naam in de UI). <paramref name="onRemoteDeviceName"/> wordt al vóór de
    /// bulkoverdracht aangeroepen (vlak na de PIN-check), zodat de UI de naam
    /// meteen kan tonen in plaats van pas te wachten tot de overdracht klaar is.
    ///
    /// Beveiliging: <paramref name="pin"/> moet overeenkomen met de PIN die de
    /// verzendende kant invoert. Zonder overeenkomende PIN wordt de
    /// verbinding geweigerd, en de hele overdracht wordt met AES-256
    /// versleuteld met een sleutel afgeleid van die PIN - zo kan een andere
    /// pc op hetzelfde (Wifi-)netwerk niet zomaar meeluisteren of zich
    /// voordoen als deze ontvanger.
    /// </summary>
    public async Task<string> ReceiveOnceAsync(string saveAsPath, string pin, IProgress<double> progress, CancellationToken ct, IProgress<string>? currentFileProgress = null, Action? onConnected = null, Action<string>? onRemoteDeviceName = null)
    {
        currentFileProgress?.Report($"Ontvangen: {Path.GetFileName(saveAsPath)}");

        var listener = new TcpListener(IPAddress.Any, DefaultTcpPort);
        listener.Start();
        try
        {
            _log.Report($"Wachten op verbinding op poort {DefaultTcpPort} ...");
            using var client = await listener.AcceptTcpClientAsync(ct);
            _log.Report($"Verbonden met {client.Client.RemoteEndPoint}. PIN controleren ...");
            onConnected?.Invoke(); // pas nú écht iets binnenkomt naar het voortgangstabblad springen, niet al bij het klikken op "Start ontvangen" (dan blijft de PIN nog zichtbaar)

            using var networkStream = client.GetStream();

            byte[] key = NetworkCrypto.DeriveKey(pin);
            byte[] iv = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(16);
            await networkStream.WriteAsync(iv, ct);
            await networkStream.WriteAsync(nonce, ct);

            byte[] expectedHmac;
            using (var hmac = new HMACSHA256(key))
                expectedHmac = hmac.ComputeHash(nonce);

            byte[] receivedHmac = new byte[32];
            await NetworkCrypto.ReadExactAsync(networkStream, receivedHmac, ct);

            bool pinOk = CryptographicOperations.FixedTimeEquals(expectedHmac, receivedHmac);
            await networkStream.WriteAsync(new[] { (byte)(pinOk ? 1 : 0) }, ct);

            if (!pinOk)
            {
                _log.Report("Verkeerde PIN van de verzendende pc - overdracht geweigerd.");
                throw new InvalidOperationException("De verzendende pc gaf een verkeerde PIN op - overdracht geweigerd.");
            }

            _log.Report("PIN klopt. Ontvangst gestart (versleuteld) ...");

            // Onversleuteld toestelnaam-veldje lezen dat de verzendende kant net na de
            // PIN-check meestuurt - compatibel met de Android-kant, die ditzelfde
            // lengte-geprefixte veld verstuurt.
            byte[] nameLenBuffer = new byte[2];
            await NetworkCrypto.ReadExactAsync(networkStream, nameLenBuffer, ct);
            int nameLen = BitConverter.ToUInt16(nameLenBuffer, 0);
            byte[] nameBuffer = new byte[nameLen];
            if (nameLen > 0) await NetworkCrypto.ReadExactAsync(networkStream, nameBuffer, ct);
            string remoteDeviceName = nameLen > 0 ? Encoding.UTF8.GetString(nameBuffer) : "Onbekend apparaat";
            _log.Report($"Toestelnaam van de zendende kant: {remoteDeviceName}");
            onRemoteDeviceName?.Invoke(remoteDeviceName);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var cryptoStream = new CryptoStream(networkStream, aes.CreateDecryptor(), CryptoStreamMode.Read);

            byte[] lengthBuffer = new byte[8];
            await NetworkCrypto.ReadExactAsync(cryptoStream, lengthBuffer, ct);
            long totalBytes = BitConverter.ToInt64(lengthBuffer, 0);

            await using var fileStream = File.Create(saveAsPath);
            byte[] buffer = new byte[81920];
            long received = 0;
            while (received < totalBytes)
            {
                int toRead = (int)Math.Min(buffer.Length, totalBytes - received);
                int read = await cryptoStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (read == 0) throw new IOException("Verbinding werd onverwacht verbroken tijdens de overdracht.");
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress.Report(totalBytes == 0 ? 1.0 : (double)received / totalBytes);
            }

            _log.Report("Ontvangst voltooid.");
            return remoteDeviceName;
        }
        finally
        {
            listener.Stop();
        }
    }

}
