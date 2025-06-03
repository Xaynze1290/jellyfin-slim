using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Jellyfin.Extensions;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace MediaBrowser.Common.Net;

/// <summary>
/// Defines the <see cref="NetworkUtils" />.
/// </summary>
public static partial class NetworkUtils
{
    // Use regular expression as CheckHostName isn't RFC5892 compliant.
    // Modified from gSkinner's expression at https://stackoverflow.com/questions/11809631/fully-qualified-domain-name-validation
    [GeneratedRegex(@"(?im)^(?!:\/\/)(?=.{1,255}$)((.{1,63}\.){0,127}(?![0-9]*$)[a-z0-9-]+\.?)(:(\d){1,5}){0,1}$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex FqdnGeneratedRegex();

    public static bool IsIPv6LinkLocal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) { return false; }
        Span<byte> octet = stackalloc byte[16];
        address.TryWriteBytes(octet, out _);
        uint word = (uint)(octet[0] << 8) + octet[1];
        return word >= 0xfe80 && word <= 0xfebf;
    }

    public static IPAddress CidrToMask(byte cidr, AddressFamily family)
    {
        uint addr = 0xFFFFFFFF << ((family == AddressFamily.InterNetwork ? NetworkConstants.MinimumIPv4PrefixSize : NetworkConstants.MinimumIPv6PrefixSize) - cidr);
        addr = ((addr & 0xff000000) >> 24) | ((addr & 0x00ff0000) >> 8) | ((addr & 0x0000ff00) << 8) | ((addr & 0x000000ff) << 24);
        return new IPAddress(addr);
    }

    public static IPAddress CidrToMask(int cidr, AddressFamily family)
    {
        uint addr = 0xFFFFFFFF << ((family == AddressFamily.InterNetwork ? NetworkConstants.MinimumIPv4PrefixSize : NetworkConstants.MinimumIPv6PrefixSize) - cidr);
        addr = ((addr & 0xff000000) >> 24) | ((addr & 0x00ff0000) >> 8) | ((addr & 0x0000ff00) << 8) | ((addr & 0x000000ff) << 24);
        return new IPAddress(addr);
    }

    public static byte MaskToCidr(IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        byte cidrnet = 0;
        if (mask.Equals(IPAddress.Any)) { return cidrnet; }
        Span<byte> bytes = stackalloc byte[mask.AddressFamily == AddressFamily.InterNetwork ? NetworkConstants.IPv4MaskBytes : NetworkConstants.IPv6MaskBytes];
        if (!mask.TryWriteBytes(bytes, out var bytesWritten)) { Console.WriteLine("Unable to write address bytes, only {0} bytes written.", bytesWritten.ToString(CultureInfo.InvariantCulture)); }
        var zeroed = false;
        for (var i = 0; i < bytes.Length; i++) {
            for (int v = bytes[i]; (v & 0xFF) != 0; v <<= 1) {
                if (zeroed) { return (byte)~cidrnet; }
                if ((v & 0x80) == 0) { zeroed = true; } else { cidrnet++; }
            }
        }
        return cidrnet;
    }

    public static string FormatIPString(IPAddress? address)
    {
        if (address is null) { return string.Empty; }
        var str = address.ToString();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            int i = str.IndexOf("%", StringComparison.Ordinal); // Changed char '%' to string "%"
            if (i != -1) { str = str.Substring(0, i); }
            return $"[{str}]";
        }
        return str;
    }

    public static bool TryParseToSubnets(string[] values, [NotNullWhen(true)] out IReadOnlyList<IPNetwork>? result, bool negated = false)
    {
        if (values is null || values.Length == 0) { result = null; return false; }
        var tmpResult = new List<IPNetwork>();
        for (int a = 0; a < values.Length; a++)
        {
            if (TryParseToSubnet(values[a], out var innerResult, negated)) { tmpResult.Add(innerResult); }
        }
        result = tmpResult;
        return tmpResult.Count > 0;
    }

    public static bool TryParseToSubnet(ReadOnlySpan<char> value, [NotNullWhen(true)] out IPNetwork? result, bool negated = false)
    {
        value = value.Trim();
        bool isAddressNegated = false;
        if (value.StartsWith('!')) { isAddressNegated = true; value = value[1..]; }
        if (isAddressNegated != negated) { result = null; return false; }
        if (value.Contains('/'))
        {
            if (IPNetwork.TryParse(value, out result)) { return true; }
        }
        else if (IPAddress.TryParse(value, out var address))
        {
            if (address.AddressFamily == AddressFamily.InterNetwork) { result = address.Equals(IPAddress.Any) ? NetworkConstants.IPv4Any : new IPNetwork(address, NetworkConstants.MinimumIPv4PrefixSize); return true; }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6) { result = address.Equals(IPAddress.IPv6Any) ? NetworkConstants.IPv6Any : new IPNetwork(address, NetworkConstants.MinimumIPv6PrefixSize); return true; }
        }
        result = null;
        return false;
    }

    public static bool TryParseHost(ReadOnlySpan<char> host, [NotNullWhen(true)] out IPAddress[]? addresses, bool isIPv4Enabled = true, bool isIPv6Enabled = false)
    {
        host = host.Trim();
        if (host.IsEmpty) { addresses = null; return false; }
        if (host[0] == '[')
        {
            int i = host.IndexOf("]"); // Ensuring this uses string "]"
            if (i != -1)
            {
                return TryParseHost(host.Slice(1, i - 1), out addresses);
            }
            addresses = Array.Empty<IPAddress>();
            return false;
        }
        var hosts = new List<string>();
        foreach (var splitSpan in host.Split(':')) { hosts.Add(splitSpan.ToString()); }
        if (hosts.Count <= 2)
        {
            var firstPart = hosts[0];
            if (FqdnGeneratedRegex().IsMatch(firstPart))
            {
                try { addresses = Dns.GetHostAddresses(firstPart); return true; }
                catch (SocketException) { /* Ignore */ }
            }
            if (IPAddress.TryParse(firstPart.AsSpan().LeftPart('/'), out var address))
            {
                if (((address.AddressFamily == AddressFamily.InterNetwork) && (!isIPv4Enabled && isIPv6Enabled)) || ((address.AddressFamily == AddressFamily.InterNetworkV6) && (isIPv4Enabled && !isIPv6Enabled)))
                { addresses = Array.Empty<IPAddress>(); return false; }
                addresses = new[] { address };
                return true;
            }
        }
        else if (hosts.Count > 0 && hosts.Count <= 9)
        {
            if (IPAddress.TryParse(host.LeftPart('/'), out var address)) { addresses = new[] { address }; return true; }
        }
        addresses = Array.Empty<IPAddress>();
        return false;
    }

    public static IPAddress GetBroadcastAddress(IPNetwork network)
    {
        var addressBytes = network.Prefix.GetAddressBytes();
        uint ipAddress = BitConverter.ToUInt32(addressBytes, 0);
        uint ipMaskV4 = BitConverter.ToUInt32(CidrToMask(network.PrefixLength, AddressFamily.InterNetwork).GetAddressBytes(), 0);
        uint broadCastIPAddress = ipAddress | ~ipMaskV4;
        return new IPAddress(BitConverter.GetBytes(broadCastIPAddress));
    }

    public static bool SubnetContainsAddress(IPNetwork network, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(network);
        if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
        return network.Contains(address);
    }
}
