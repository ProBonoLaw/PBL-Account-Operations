using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace ResourceManager.Domain;

public static class Ipv4CidrAllocator
{
    public static string FindFirstAvailableSubnet(
        IEnumerable<string> addressSpaces,
        IEnumerable<string> existingSubnets,
        int desiredPrefixLength)
    {
        ArgumentNullException.ThrowIfNull(addressSpaces);
        ArgumentNullException.ThrowIfNull(existingSubnets);
        if (desiredPrefixLength is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(desiredPrefixLength), desiredPrefixLength,
                "The desired IPv4 prefix length must be between 0 and 32.");

        var spaces = addressSpaces.Select(ParseIpv4Cidr).Where(range => range is not null)
            .Select(range => range!.Value).ToArray();
        var occupied = existingSubnets.Select(ParseIpv4Cidr).Where(range => range is not null)
            .Select(range => range!.Value).ToArray();
        var blockSize = BigInteger.One << (32 - desiredPrefixLength);

        foreach (var space in spaces)
        {
            if (desiredPrefixLength < space.PrefixLength)
                continue;

            for (var start = space.Start; start + blockSize - 1 <= space.End; start += blockSize)
            {
                var end = start + blockSize - 1;
                if (occupied.Any(existing => start <= existing.End && existing.Start <= end))
                    continue;

                return $"{ToIpv4Address(start)}/{desiredPrefixLength}";
            }
        }

        throw new InvalidOperationException(
            $"No available /{desiredPrefixLength} subnet found inside the supplied IPv4 address spaces.");
    }

    private static Ipv4Range? ParseIpv4Cidr(string cidr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidr);
        var parts = cidr.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)
        {
            if (!cidr.Contains('.'))
                return null;
            throw new FormatException($"Invalid IPv4 CIDR: {cidr}");
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var addressValue = new BigInteger(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
        var blockSize = BigInteger.One << (32 - prefix);
        var start = addressValue / blockSize * blockSize;
        return new Ipv4Range(start, start + blockSize - 1, prefix);
    }

    private static IPAddress ToIpv4Address(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var addressBytes = new byte[4];
        bytes.CopyTo(addressBytes, 4 - bytes.Length);
        return new IPAddress(addressBytes);
    }

    private readonly record struct Ipv4Range(BigInteger Start, BigInteger End, int PrefixLength);
}
