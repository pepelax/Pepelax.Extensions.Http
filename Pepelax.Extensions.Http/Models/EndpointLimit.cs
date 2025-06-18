namespace Pepelax.Extensions.Http;

public record EndpointLimit
{
    // Шаблон для URL, например "https://api.myservice.com/v1/users/*"
    // Простая реализация может использовать StartsWith.
    public required string Pattern { get; init; }

    public int Limit { get; init; }
    public int WindowSeconds { get; init; }
}