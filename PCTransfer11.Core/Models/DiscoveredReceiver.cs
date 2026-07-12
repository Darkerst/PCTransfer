namespace PCTransfer11.Models;

/// <summary>Eén ontvangende pc die via netwerk-discovery is gevonden.</summary>
public sealed record DiscoveredReceiver(string IpAddress, string HostName, int TcpPort)
{
    public override string ToString() => $"{HostName} ({IpAddress})";
}
