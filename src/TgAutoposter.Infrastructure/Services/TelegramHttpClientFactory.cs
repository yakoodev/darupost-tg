using System.Net;
using Microsoft.Extensions.Options;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public sealed class TelegramHttpClientFactory(IOptions<TelegramOptions> optionsAccessor)
{
    public HttpClient CreateClient()
    {
        var options = optionsAccessor.Value;
        var handler = new HttpClientHandler();

        if (options.Proxy.Enabled)
        {
            handler.Proxy = CreateProxy(options.Proxy);
            handler.UseProxy = true;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    private static IWebProxy CreateProxy(ProxyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host) || options.Port <= 0)
        {
            throw new InvalidOperationException("Telegram proxy is enabled, but host or port is not configured.");
        }

        var scheme = options.Type.Equals("Socks5", StringComparison.OrdinalIgnoreCase)
            ? "socks5"
            : "http";

        var proxy = new WebProxy(new Uri($"{scheme}://{options.Host}:{options.Port}"));

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            proxy.Credentials = new NetworkCredential(options.Username, options.Password);
        }

        return proxy;
    }
}
