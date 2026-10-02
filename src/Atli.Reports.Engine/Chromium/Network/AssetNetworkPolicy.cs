using System.Net;
using System.Net.Sockets;

namespace Atli.Reports.Engine.Chromium.Network;

internal sealed class AssetNetworkPolicy(ReportsEngineNetworkOptions options)
{
  private readonly HashSet<string> _origins = options
    .AllowedOrigins.Select(origin => Origin(new Uri(origin, UriKind.Absolute)))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

  public bool Allows(Uri uri) =>
    options.Mode == ReportsEngineNetworkMode.AllowList
    && uri.Scheme is "http" or "https"
    && string.IsNullOrEmpty(uri.UserInfo)
    && _origins.Contains(Origin(uri));

  public static bool IsValidOrigin(string value) =>
    Uri.TryCreate(value, UriKind.Absolute, out var uri)
    && uri.Scheme is "http" or "https"
    && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
    && string.IsNullOrEmpty(uri.UserInfo)
    && uri.AbsolutePath == "/"
    && string.IsNullOrEmpty(uri.Query)
    && string.IsNullOrEmpty(uri.Fragment);

  private static string Origin(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";

  // Conservative global-unicast filter. Infrastructure egress rules are still required: these
  // checks do not know about a customer's routes, public VIPs, or a compromised renderer process.
  internal static bool IsPublicAddress(IPAddress address)
  {
    if (address.IsIPv4MappedToIPv6)
    {
      address = address.MapToIPv4();
    }

    var bytes = address.GetAddressBytes();
    if (address.AddressFamily == AddressFamily.InterNetwork)
    {
      return bytes[0] is not (0 or 10 or 127)
        && bytes[0] < 224
        && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
        && !(bytes[0] == 169 && bytes[1] == 254)
        && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        && !(bytes[0] == 192 && bytes[1] == 168)
        && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
        && !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
        && !(bytes[0] == 198 && bytes[1] is 18 or 19)
        && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
        && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
        // Azure's platform virtual address is public-looking but exposes host services.
        && !address.Equals(IPAddress.Parse("168.63.129.16"));
    }

    return address.AddressFamily == AddressFamily.InterNetworkV6
      && address.ScopeId == 0
      && (bytes[0] & 0xe0) == 0x20
      && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2)
      && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
      && !(bytes[0] == 0x20 && bytes[1] == 0x02)
      && !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
  }
}
