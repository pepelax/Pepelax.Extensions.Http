using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Pepelax.Extensions.Http;

public class ProxyManager : IProxyManager, IDisposable
{
    private readonly IOptionsMonitor<ProxyRotationOptions> _optionsMonitor;
    private List<ProxyState> _proxies;
    private readonly Lock _lock = new();
    private readonly IDisposable _optionsChangeListener;

    public ProxyManager(IOptionsMonitor<ProxyRotationOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
        _optionsChangeListener = _optionsMonitor.OnChange(UpdateProxies);
        UpdateProxies(_optionsMonitor.CurrentValue);
    }

    private void UpdateProxies(ProxyRotationOptions options)
    {
        var newProxies = new List<ProxyState>();

        // Если список прокси пуст, создаем один "пустой" ProxyState для прямых запросов
        if (options.Proxies == null || !options.Proxies.Any())
        {
            // Для прямых запросов используем Default лимит, если он есть. Если нет - лимита не будет.
            var directLimiter = CreateRateLimiter(options.DefaultProxyLimit, options.DefaultProxyWindowSeconds);
            newProxies.Add(new ProxyState(null, directLimiter));
        }
        else
        {
            foreach (var proxyConfig in options.Proxies)
            {
                if (string.IsNullOrEmpty(proxyConfig.Address)) continue;

                // Определяем лимит для этого прокси: свой > общий > нет лимита
                var limit = proxyConfig.Limit ?? options.DefaultProxyLimit;
                var window = proxyConfig.WindowSeconds ?? options.DefaultProxyWindowSeconds;

                var rateLimiter = CreateRateLimiter(limit, window);
                newProxies.Add(new ProxyState(proxyConfig.Address, rateLimiter));
            }
        }

        lock (_lock)
        {
            var oldProxies = _proxies;
            _proxies = newProxies;

            if (oldProxies != null)
            {
                foreach (var p in oldProxies) p.Dispose();
            }
        }
    }

    // Вспомогательный метод для создания RateLimiter или null
    private static TokenBucketRateLimiter? CreateRateLimiter(int? limit, int? windowSeconds)
    {
        if (!limit.HasValue || limit <= 0 || !windowSeconds.HasValue || windowSeconds < 0)
            return null;

        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = limit.Value,
            TokensPerPeriod = limit.Value,
            ReplenishmentPeriod = TimeSpan.FromSeconds(windowSeconds.Value),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = int.MaxValue
        });
    }

    public IReadOnlyList<ProxyState> GetRankedProxies()
    {
        lock (_lock)
        {
            // Сортируем по убыванию Score. Те, что лучше, будут первыми.
            return _proxies.OrderByDescending(p => p.Score).ToList();
        }
    }

    public void Dispose()
    {
        _optionsChangeListener?.Dispose();
        lock (_lock)
        {
            if (_proxies != null)
            {
                foreach (var p in _proxies) p.Dispose();
            }
        }
    }
}
