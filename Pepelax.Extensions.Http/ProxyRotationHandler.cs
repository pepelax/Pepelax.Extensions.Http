using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Pepelax.Extensions.Http;

public class ProxyRotationHandler(
    IProxyManager proxyManager,
    IEndpointRateLimiterManager endpointLimiterManager,
    ILogger<ProxyRotationHandler> logger
    ) : DelegatingHandler
{
    private readonly IProxyManager _proxyManager = proxyManager;
    private readonly IEndpointRateLimiterManager _endpointLimiterManager = endpointLimiterManager;
    private readonly ILogger<ProxyRotationHandler> _logger = logger;

    // Cоздаем ключ для опций один раз и переиспользуем его. Это стандартная практика.
    private static readonly HttpRequestOptionsKey<IWebProxy> ProxyOptionsKey = new("Proxy");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // --- ШАГ 1: Лимитирование на уровне эндпоинта ---
        var endpointLimiter = _endpointLimiterManager.GetRateLimiterFor(request.RequestUri);
        RateLimitLease? endpointLease = null;

        if (endpointLimiter != null)
        {
            _logger.LogTrace("Awaiting endpoint rate limit for {Url}", request.RequestUri);
            endpointLease = await endpointLimiter.AcquireAsync(1, cancellationToken);
            if (!endpointLease.IsAcquired)
            {
                throw new HttpRequestException($"Failed to acquire rate limit lease for endpoint: {request.RequestUri}. The operation might have been cancelled or the limiter disposed.");
            }
            _logger.LogTrace("Endpoint rate limit acquired for {Url}", request.RequestUri);
        }

        try
        {
            // --- ШАГ 2: Выбор прокси и лимитирование на уровне прокси ---
            var rankedProxies = _proxyManager.GetRankedProxies();
            if (!rankedProxies.Any())
            {
                _logger.LogWarning("No proxies configured. Sending request directly.");
                return await base.SendAsync(request, cancellationToken);
            }

            var exceptions = new List<Exception>();

            foreach (var proxyState in rankedProxies)
            {
                RateLimitLease? proxyLease = null;
                try
                {
                    // --- НОВОЕ: Проверяем, есть ли лимитер для этого прокси ---
                    if (proxyState.RateLimiter is not null)
                    {
                        _logger.LogTrace("Awaiting proxy rate limit for {Proxy}", proxyState.Address ?? "Direct");
                        proxyLease = await proxyState.RateLimiter.AcquireAsync(1, cancellationToken);
                        if (!proxyLease.IsAcquired)
                        {
                            var ex = new HttpRequestException($"Failed to acquire rate limit lease for proxy: {proxyState.Address}.");
                            exceptions.Add(ex);
                            continue;
                        }
                        _logger.LogTrace("Proxy rate limit acquired for {Proxy}", proxyState.Address ?? "Direct");
                    }
                    else
                    {
                        _logger.LogTrace("No rate limit for proxy {Proxy}. Proceeding immediately.", proxyState.Address ?? "Direct");
                    }

                    // 1. Клонируем запрос. Это обязательно, так как мы будем менять его опции для каждой попытки,
                    // а HttpRequestMessage можно отправить только один раз.
                    var clonedRequest = await CloneHttpRequestMessageAsync(request);

                    // 2. Устанавливаем IWebProxy (или null для прямого соединения) в опции запроса.
                    // SocketsHttpHandler, находящийся ниже в конвейере, увидит эту опцию и использует ее.
                    if (proxyState.Proxy is not null)
                        clonedRequest.Options.Set(ProxyOptionsKey, proxyState.Proxy);

                    _logger.LogInformation("Sending request to {Url} via proxy: {ProxyAddress}", clonedRequest.RequestUri, proxyState.Address ?? "Direct connection");
                    var stopwatch = Stopwatch.StartNew();

                    // 3. Отправляем *клонированный* запрос с установленной опцией. `base.SendAsync` передаст его
                    // нашему `SocketsHttpHandler`, который использует правильный прокси.
                    var response = await base.SendAsync(clonedRequest, cancellationToken);

                    stopwatch.Stop();
                    proxyState.RecordSuccess(stopwatch.Elapsed);
                    _logger.LogInformation("Request to {Url} succeeded via {ProxyAddress} in {Elapsed}ms",
                                           request.RequestUri, proxyState.Address ?? "Direct connection", stopwatch.ElapsedMilliseconds);

                    return response;
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    _logger.LogWarning(ex, "Request failed via proxy {ProxyAddress}. Trying next.", proxyState.Address ?? "Direct connection");
                    proxyState.RecordFailure();
                    exceptions.Add(ex);
                }
                finally
                {
                    proxyLease?.Dispose(); // Освобождаем лимит прокси для этой попытки
                }
            }

            throw new AggregateException("All proxies failed to process the request.", exceptions);
        }
        finally
        {
            endpointLease?.Dispose(); // Освобождаем лимит эндпоинта после завершения всех попыток
        }
    }

    // Вспомогательный метод для клонирования HttpRequestMessage
    private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage originalRequest)
    {
        var clone = new HttpRequestMessage(originalRequest.Method, originalRequest.RequestUri);

        if (originalRequest.Content is not null)
        {
            var ms = new MemoryStream();
            await originalRequest.Content.CopyToAsync(ms);
            ms.Position = 0;
            clone.Content = new StreamContent(ms);

            // Копируем заголовки контента
            if (originalRequest.Content.Headers is not null)
            {
                foreach (var header in originalRequest.Content.Headers)
                {
                    clone.Content.Headers.Add(header.Key, header.Value);
                }
            }
        }

        clone.Version = originalRequest.Version;

        foreach (var header in originalRequest.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in originalRequest.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        return clone;
    }
}
