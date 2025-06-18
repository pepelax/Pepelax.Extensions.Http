using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using Polly;

namespace Pepelax.Extensions.Http;

public static class ProxyRotationHttpClientExtensions
{
    public static IServiceCollection AddProxyRotationHttpClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ProxyRotationOptions>(configuration.GetSection(ProxyRotationOptions.SectionName));

        // Регистрируем наши зависимости
        services.AddSingleton<IProxyManager, ProxyManager>();
        services.AddSingleton<IEndpointRateLimiterManager, EndpointRateLimiterManager>();
        
        // Регистрируем наш класс, который создает стратегию
        services.AddSingleton<ProxyResilienceStrategy>();

        services.AddHttpClient(Options.DefaultName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = true })
            // --- НОВЫЙ СПОСОБ ДОБАВЛЕНИЯ ЛОГИКИ ---
            .AddResilienceHandler("proxy-rotation-pipeline", (builder, context) =>
            {
                // Получаем наш сервис, который умеет создавать пайплайн
                var strategyProvider = context.ServiceProvider.GetRequiredService<ProxyResilienceStrategy>();
                
                // Создаем и конфигурируем пайплайн
                builder.AddPipeline(strategyProvider.CreatePipeline());
            });

        return services;
    }
    
    public static IServiceCollection AddProxyRotationHttpClientOld(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 1. Регистрируем конфигурацию
        services.Configure<ProxyRotationOptions>(configuration.GetSection(ProxyRotationOptions.SectionName));

        // 2. Регистрируем менеджер как Singleton, чтобы он хранил состояние между запросами
        services.AddSingleton<IProxyManager, ProxyManager>();
        services.AddSingleton<IEndpointRateLimiterManager, EndpointRateLimiterManager>();

        // 3. Регистрируем наш handler как Transient
        services.AddTransient<ProxyRotationHandler>();

        // Настраиваем HttpClient по умолчанию.
        services.AddHttpClient(Options.DefaultName)
            // 1. Указываем, что в качестве основного обработчика мы будем использовать SocketsHttpHandler.
            //    IHttpClientFactory будет управлять его временем жизни и пулом соединений.
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                return new SocketsHttpHandler
                {
                    // 2. Это критически важно! Разрешаем handler'у использовать прокси.
                    //    Если false, он будет игнорировать любые настройки прокси.
                    UseProxy = true,

                    // SocketsHttpHandler имеет множество других настроек для тонкого тюнинга
                    // производительности, например, PooledConnectionLifetime, MaxConnectionsPerServer и т.д.
                    // Для нашей задачи достаточно UseProxy.
                };
            })
            // 3. Добавляем наш DelegatingHandler в конвейер. Он будет выполняться ПЕРЕД SocketsHttpHandler.
            .AddHttpMessageHandler<ProxyRotationHandler>();


        // Можно также создать именованный клиент, если нужно
        // services.AddHttpClient("MyProxyClient")
        //     .AddHttpMessageHandler<ProxyRotationHandler>();

        return services;
    }
}
