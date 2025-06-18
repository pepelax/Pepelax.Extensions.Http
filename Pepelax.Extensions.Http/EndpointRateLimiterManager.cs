using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Pepelax.Extensions.Http;

public class EndpointRateLimiterManager : IEndpointRateLimiterManager, IDisposable
{
    private readonly IOptionsMonitor<ProxyRotationOptions> _optionsMonitor;
    private IDisposable? _optionsChangeListener;
    // Словарь для хранения лимитеров: ключ - EndpointLimit, значение - RateLimiter
    private ConcurrentDictionary<EndpointLimit, RateLimiter> _limiters = new();

    public EndpointRateLimiterManager(IOptionsMonitor<ProxyRotationOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
        _optionsChangeListener = _optionsMonitor.OnChange(UpdateEndpointLimiters);
        UpdateEndpointLimiters(_optionsMonitor.CurrentValue);
    }

    private void UpdateEndpointLimiters(ProxyRotationOptions options)
    {
        var oldLimiters = _limiters;
        var newLimiters = new ConcurrentDictionary<EndpointLimit, RateLimiter>();

        if (options.Endpoints != null)
        {
            foreach (var endpointConfig in options.Endpoints)
            {
                var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = endpointConfig.Limit,
                    TokensPerPeriod = endpointConfig.Limit,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(endpointConfig.WindowSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = int.MaxValue
                });
                newLimiters.TryAdd(endpointConfig, limiter);
            }
        }

        _limiters = newLimiters;

        // Освобождаем ресурсы старых лимитеров
        foreach (var limiter in oldLimiters.Values)
        {
            limiter.Dispose();
        }
    }

    public RateLimiter? GetRateLimiterFor(Uri? requestUri)
    {
        if (requestUri is null)
            return null;

        // Находим наиболее подходящий шаблон.
        // Здесь можно реализовать сложную логику, но начнем с простого StartsWith.
        var absoluteUri = requestUri.AbsoluteUri;

        // Ищем наиболее конкретное совпадение (самый длинный шаблон)
        EndpointLimit? bestMatch = null;
        foreach (var limiterConfig in _limiters.Keys)
        {
            var pattern = limiterConfig.Pattern;
            if (pattern.EndsWith('*'))
            {
                if (absoluteUri.StartsWith(pattern.TrimEnd('*')) && (bestMatch is null || pattern.Length > bestMatch.Pattern.Length))
                {
                    bestMatch = limiterConfig;
                }
            }
            else
            {
                if (absoluteUri.Equals(pattern, StringComparison.OrdinalIgnoreCase) && (bestMatch is null || pattern.Length > bestMatch.Pattern.Length))
                {
                    bestMatch = limiterConfig;
                }
            }
        }

        var limiter = (bestMatch is not null && _limiters.TryGetValue(bestMatch, out var rateLimiter))
            ? rateLimiter
            : null;
        return limiter;
    }

    public void Dispose()
    {
        _optionsChangeListener?.Dispose();
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }
}
