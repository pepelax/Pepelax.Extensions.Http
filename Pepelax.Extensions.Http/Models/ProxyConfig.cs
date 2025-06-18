namespace Pepelax.Extensions.Http;

// Описывает конфигурацию одного прокси
public record ProxyConfig
{
    public required string Address { get; init; }

    // Лимит запросов для этого конкретного прокси (необязательный)
    public int? Limit { get; init; }

    // Окно в секундах для этого конкретного прокси (необязательный)
    public int? WindowSeconds { get; init; }
}
