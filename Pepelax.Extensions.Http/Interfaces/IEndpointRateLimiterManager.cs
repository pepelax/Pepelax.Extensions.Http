using System.Threading.RateLimiting;

namespace Pepelax.Extensions.Http;

public interface IEndpointRateLimiterManager
{
    // Находит подходящий RateLimiter для данного URI
    RateLimiter? GetRateLimiterFor(Uri? requestUri);
}
