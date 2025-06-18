namespace Pepelax.Extensions.Http;

public interface IProxyManager
{
    // Возвращает отсортированный по "здоровью" список доступных прокси
    IReadOnlyList<ProxyState> GetRankedProxies();
}
