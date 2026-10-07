using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.JellyScore;

public static class TvThemeSource
{
    private const string IdPlaceholder = "{tvdbId}";
    // Exclude private, loopback, link-local, and reserved destinations from administrator-supplied URLs.
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4")
    ];
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(JellyScoreConstants.ThemeSourceConnectTimeoutSeconds),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
                !BlockedNetworks.Any(network => network.Contains(ip)));
            if (address is null) throw new HttpRequestException("TV theme host has no public IPv4 address.");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    });

    public static bool ValidTemplate(string template)
    {
        if (template.Length > JellyScoreConstants.MaximumThemeUrlLength) return false;
        var position = template.IndexOf(IdPlaceholder, StringComparison.Ordinal);
        var pathStart = template.IndexOf('/', "https://".Length);
        if (position < pathStart || position < 0 || template.IndexOf(IdPlaceholder, position + IdPlaceholder.Length, StringComparison.Ordinal) >= 0 ||
            template.Replace(IdPlaceholder, "", StringComparison.Ordinal).IndexOfAny(['{', '}']) >= 0 ||
            !Uri.TryCreate(template.Replace(IdPlaceholder, "123456", StringComparison.Ordinal), UriKind.Absolute, out var uri)) return false;
        return ValidUrl(uri.AbsoluteUri);
    }

    internal static bool ValidUrl(string url)
    {
        if (url.Length > JellyScoreConstants.MaximumThemeUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.Host.Length > 0 &&
            !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
            (!IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily == AddressFamily.InterNetwork &&
                !BlockedNetworks.Any(network => network.Contains(ip)));
    }

    public static Uri? Url(string template, string tvdbId) => ValidTemplate(template) && tvdbId.Length is > 0 and <= 12 &&
        tvdbId.All(char.IsAsciiDigit) ? new Uri(template.Replace(IdPlaceholder, tvdbId, StringComparison.Ordinal)) : null;

    public static async Task Download(Uri url, string destination, CancellationToken ct)
    {
        if (!ValidUrl(url.AbsoluteUri)) throw new DownloadFailure("The saved TV theme source URL is not allowed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(JellyScoreConstants.ThemeSourceTimeoutSeconds));
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"TV theme source returned {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > JellyScoreConstants.RawAudioMaximumBytes)
            throw new IOException("TV theme audio is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = File.Create(destination);
        var buffer = new byte[81920];
        var total = 0L;
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            total += read;
            if (total > JellyScoreConstants.RawAudioMaximumBytes) throw new IOException("TV theme audio is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
        if (total == 0) throw new IOException("TV theme source returned empty audio.");
    }
}
