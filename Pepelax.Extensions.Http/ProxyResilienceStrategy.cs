using System.Net;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.RateLimiting;
using Polly.Hedging;
using System.Threading.RateLimiting;

namespace Pepelax.Extensions.Http;

// Вспомогательный класс для передачи состояния между попытками
internal record ProxyAttemptContext
{
    public required ProxyState SelectedProxy { get; init; }
}

public class ProxyResilienceStrategy
{
    private static readonly HttpRequestOptionsKey<IWebProxy> ProxyOptionsKey = new("Proxy");

    private readonly ILogger<ProxyResilienceStrategy> _logger;
    private readonly IProxyManager _proxyManager;
    private readonly IEndpointRateLimiterManager _endpointLimiterManager;

    public ProxyResilienceStrategy(
        ILogger<ProxyResilienceStrategy> logger,
        IProxyManager proxyManager,
        IEndpointRateLimiterManager endpointLimiterManager)
    {
        _logger = logger;
        _proxyManager = proxyManager;
        _endpointLimiterManager = endpointLimiterManager;
    }

    // Основной метод, который будет создавать нашу стратегию
    public ResiliencePipeline<HttpResponseMessage> CreatePipeline()
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            // Добавляем лимитер для эндпоинтов (выполняется один раз перед всеми попытками)
            .AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args =>
                {
                    // Динамически получаем лимитер для текущего запроса
                    var limiter = _endpointLimiterManager.GetRateLimiterFor(args.Context.GetRequestMessage().RequestUri);
                    _logger.LogTrace("Endpoint limiter for {uri}: {limiterExists}",
                        args.Context.GetRequestMessage().RequestUri, limiter != null);

                    // Возвращаем RateLimiter в виде ValueTask - это корректно
                    return new ValueTask<RateLimiter>(limiter);
                }
            })
            // Добавляем нашу основную логику выбора прокси и ретраев через Hedging
            .AddHedging(new HedgingStrategyOptions<HttpResponseMessage>
            {
                // Указываем, какие результаты считать неудачей и запускать следующую попытку
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),

                // Максимальное количество попыток = количество доступных прокси
                MaxHedgedAttempts = args => new ValueTask<int>(_proxyManager.GetRankedProxies().Count),

                // Основная логика, выполняется перед каждой попыткой
                OnHedging = async args =>
                {
                    // Получаем отсортированный список прокси для каждой попытки хеджирования.
                    // args.AttemptNumber начинается с 0
                    var rankedProxies = _proxyManager.GetRankedProxies();
                    if (args.AttemptNumber >= rankedProxies.Count)
                    {
                        // Этого не должно случиться, но на всякий случай
                        _logger.LogWarning("Hedging attempt {attempt} is out of proxy range {count}",
                            args.AttemptNumber, rankedProxies.Count);
                        return;
                    }

                    var proxyState = rankedProxies[args.AttemptNumber];

                    // Сохраняем выбранный прокси в контексте для использования в других частях стратегии
                    var context = args.Context.GetOrCreate(() => new ProxyAttemptContext());
                    context.SelectedProxy = proxyState;

                    _logger.LogInformation(
                        "Attempt {attempt}: trying proxy {proxy}",
                        args.AttemptNumber + 1, proxyState.Address ?? "Direct"
                    );

                    // 1. Применяем лимит самого прокси
                    if (proxyState.RateLimiter != null)
                    {
                        await proxyState.RateLimiter.AcquireAsync(1, args.Context.CancellationToken);
                    }

                    // 2. Устанавливаем прокси в HttpRequestMessage
                    // Polly v8 делает клон запроса за нас, мы можем безопасно его модифицировать!
                    var request = args.Context.GetRequestMessage();
                    if (proxyState.Proxy != null)
                    {
                        request.Options.Set(ProxyOptionsKey, proxyState.Proxy);
                    }
                },

                // Логика после выполнения попытки
                OnHedged = args =>
                {
                    var context = args.Context.GetProxyAttemptContext();
                    if (context.SelectedProxy == null) return new ValueTask();

                    var stopwatch = Stopwatch.StartNew();
                    stopwatch.Stop(); // Пример, нам нужно будет получить реальное время

                    if (args.Outcome.Exception != null)
                    {
                        _logger.LogWarning(args.Outcome.Exception, "Attempt via {proxy} failed", context.SelectedProxy.Address ?? "Direct");
                        context.SelectedProxy.RecordFailure();
                    }
                    else
                    {
                        // ВАЖНО: Polly Hedging не предоставляет простого способа измерить время
                        // конкретной попытки. Это ограничение. Мы можем записать только успех/неудачу.
                        // Если замер времени критичен, придется вернуться к DelegatingHandler,
                        // но для большинства случаев простого ранжирования по % успеха достаточно.
                        _logger.LogInformation("Attempt via {proxy} succeeded", context.SelectedProxy.Address ?? "Direct");
                        context.SelectedProxy.RecordSuccess(TimeSpan.Zero); // Записываем успех без времени
                    }

                    return new ValueTask();
                }
            })
            .Build();
    }
}

// Вспомогательные extension-методы для удобной работы с контекстом
internal static class ResilienceContextExtensions
{
    private static readonly ContextItemKey<HttpRequestMessage> RequestMessageKey = new("RequestMessage");

    public static HttpRequestMessage GetRequestMessage(this ResilienceContext context) => context.Properties.GetValue(RequestMessageKey, null);

    public static ProxyAttemptContext GetProxyAttemptContext(this ResilienceContext context) => context.Properties.GetValue(new ContextItemKey<ProxyAttemptContext>("ProxyContext"), null);
    public static ProxyAttemptContext GetOrCreate(this ResilienceContext context, Func<ProxyAttemptContext> factory)
    {
        if (context.Properties.TryGetValue(new ContextItemKey<ProxyAttemptComponent>("ProxyContext"), out var component))
        {
            return component;
        }

        var newComponent = factory();
        context.Properties.Set(new ContextItemKey<ProxyAttemptComponent>("ProxyContext"), newComponent);
        return newComponent;
    }
}
