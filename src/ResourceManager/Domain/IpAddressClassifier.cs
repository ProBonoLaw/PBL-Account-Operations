using System.Net;
using System.Net.Sockets;

namespace ResourceManager.Domain;

public static class IpAddressClassifier
{
    public static bool IsPrivate(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var octets = parsed.GetAddressBytes();
        return octets[0] == 10 ||
               (octets[0] == 172 && octets[1] is >= 16 and <= 31) ||
               (octets[0] == 192 && octets[1] == 168);
    }
}
