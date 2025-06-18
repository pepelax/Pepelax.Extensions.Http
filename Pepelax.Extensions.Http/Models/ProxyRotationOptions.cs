namespace Pepelax.Extensions.Http;

public record ProxyRotationOptions
{
    public const string SectionName = nameof(ProxyRotationOptions);

    // Список прокси
    public List<ProxyConfig> Proxies { get; init; } = [];

    // Умолчательный лимит для прокси, если не указан специфичный
    public int? DefaultProxyLimit { get; init; } = null;

    // Умолчательное окно для прокси, если не указано специфичное
    public int? DefaultProxyWindowSeconds { get; init; } = null;

    // Список специфичных лимитов для эндпоинтов
    public List<EndpointLimit> Endpoints { get; init; } = [];
}