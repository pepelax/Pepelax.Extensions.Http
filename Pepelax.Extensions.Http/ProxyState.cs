using System.Net;
using System.Threading.RateLimiting;

namespace Pepelax.Extensions.Http;

public class ProxyState : IDisposable
{
    private long _successCount;
    private long _failureCount;
    private long _totalLatencyTicks; // Для подсчета среднего времени отклика

    public WebProxy? Proxy { get; }
    public RateLimiter? RateLimiter { get; }
    public string? Address { get; }

    public ProxyState(string? address, RateLimiter? rateLimiter)
    {
        Address = address;
        RateLimiter = rateLimiter;
        if (!string.IsNullOrEmpty(address))
        {
            Proxy = new WebProxy(new Uri(address));
        }
    }

    // Ранжирующий скор: выше — лучше.
    // Приоритет у прокси с высоким % успеха и низким временем отклика.
    // Добавляем 1 к знаменателю, чтобы избежать деления на ноль.
    public double Score => SuccessRate * 1000 / (AverageLatency.TotalMilliseconds + 1);

    public double SuccessRate => (_successCount + _failureCount) == 0 ? 1.0 : (double)_successCount / (_successCount + _failureCount);
    public long SuccessCount => Interlocked.Read(ref _successCount);
    public long FailureCount => Interlocked.Read(ref _failureCount);
    public TimeSpan AverageLatency => _successCount == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(Interlocked.Read(ref _totalLatencyTicks) / Interlocked.Read(ref _successCount));

    public void RecordSuccess(TimeSpan latency)
    {
        Interlocked.Increment(ref _successCount);
        Interlocked.Add(ref _totalLatencyTicks, latency.Ticks);
    }

    public void RecordFailure()
    {
        Interlocked.Increment(ref _failureCount);
    }

    public void Dispose()
    {
        RateLimiter?.Dispose();
    }
}
