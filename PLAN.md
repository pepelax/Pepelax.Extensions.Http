# Implementation Plan for Enhanced HttpClient with Proxy Rotation & Rate Limits

## Overview

This plan outlines how to implement an ASP.NET Core library that integrates seamlessly with dependency injection and `IHttpClientFactory` to provide:
1. Dynamic proxy rotation.
2. Per-proxy and per-endpoint rate limiting.
3. Retry logic for failed requests through successive proxies.
4. A fallback rate limit for direct (non-proxy) requests.

The library will read configuration from `appsettings.json`, with the following items:
- A list of proxies (each with its own RPS limit).
- A global RPS limit for direct/unmatched endpoint requests.
- A set of endpoint-specific RPS limits, each referencing a URL pattern.

## Key Requirements

1. There are two kinds of Rate Limiting (RPS):
   - **Proxy RPS**: Each proxy has a global limit (e.g., 1000 requests/min), enforced to avoid exceeding the provider’s constraints.
   - **Endpoint RPS**: Each endpoint may have its own limits (e.g., 5 requests/10s). When a request is routed, both the proxy’s RPS limit and the endpoint’s RPS limit must be respected.

2. **Proxy Ranking and Selection**: For each request, the handler must:
   - Examine proxies that are “available” (i.e., successful in the last 5 attempts).
   - Among available proxies, pick the one with the best (lowest) average response time.
   - If a request fails when using a proxy, retry with the next best candidate until success or all proxies fail.

3. **Default/Global RPS**: If no proxy is used and no specific endpoint rule matches, a global/unmatched RPS limit applies. If not specified, it can be treated as unlimited.

4. **Integration with DI**: Services simply configure the library in `Program.cs` or `Startup.cs`:
   ```csharp
   services.AddHttpClient()
       .AddHttpMessageHandler<ProxyRotationHandler>();
   ```
   or by naming the client. The logic should be hidden behind custom extension methods and a specialized message handler.

## High-Level Flow

```mermaid
flowchart LR
    A[Request from HttpClient] --> B[ProxyRotationHandler]
    B --> C[TokenBucket for Endpoint]
    C --> D[TokenBucket for Proxy]
    D --> E{Select Proxy by Availability + Speed}
    E -->|Assign Proxy| F[Send Request]
    F --> G{Response}
    G -->|Success| H[Update Stats (Availability & Speed)]
    G -->|Failure| E[Choose Next Proxy]
    H --> I[Return Response]
```

1. **Receive Request** via the custom handler or an HttpMessageInvoker.
2. **Enforce Endpoint RPS** (token bucket or rate limiter).
3. **Enforce Proxy RPS** (token bucket or rate limiter) for the chosen proxy.
4. **Select an Available Proxy**, sorted by average request time (efficiency).
5. **Send the HTTP Request**.  
   - **On success**: Mark a success, update average time.  
   - **On failure**: Mark a failure, try the next available proxy.
6. **Return Response** to caller or throw if all proxies failed.

## Implementation Steps

1. **Configuration Models**  
   - `ProxyRotationOptions` – contains global/unmatched RPS, list of proxies, and endpoint-specific limits.  
   - `ProxyConfig` – each proxy’s address, credentials, and max RPS.  
   - `EndpointLimit` – identifies a route pattern and the RPS rules for that route.

2. **Rate Limiter Management**  
   - **EndpointRateLimiterManager** – stores and retrieves rate limiters for each endpoint pattern.  
   - **ProxyManager** – manages proxies, including each proxy’s rate limiter and stats.

3. **Proxy Availability & Stats**  
   - Maintain rolling metrics: last 5 request outcomes (success/failure) and average response time.  
   - “Available” means at least 5 consecutive successes or no failures in recent memory. Among these, pick the lowest average response time.

4. **Message Handler** (`ProxyRotationHandler`)  
   - Intercept requests.  
   - Determine which endpoint limit applies.  
   - Acquire a token from the endpoint’s rate limiter; wait if needed.  
   - Select the best proxy.  
   - Acquire a token from that proxy’s rate limiter; wait if needed.  
   - Execute the request.  
   - If it fails, mark the proxy as failing, then retry with next.  
   - Return the success or failure to the caller.

5. **Dependency Injection Setup**  
   - Add an extension, e.g. `services.AddProxyRotationHttpClient(options => {...})` that reads from `appsettings.json`.  
   - Configure the `ProxyRotationHandler` in DI so that it knows how to read from the managers.

6. **Example**  
   - Provide a sample console or worker project demonstrating usage, with an `appsettings.json` specifying:  
     ```json
     {
       "ProxyRotationOptions": {
         "GlobalRps": {
           "Count": 100,
           "IntervalSeconds": 60
         },
         "Proxies": [
           { "Address": "http://proxy1:8080", "MaxRequests": 1000, "IntervalSeconds": 60 },
           { "Address": "http://proxy2:8080", "MaxRequests": 1000, "IntervalSeconds": 60 }
         ],
         "Endpoints": [
           {
             "Pattern": "api.example.com/endpoint1",
             "RpsCount": 5,
             "IntervalSeconds": 10
           },
           {
             "Pattern": "api.example.com/endpoint2",
             "RpsCount": 10,
             "IntervalSeconds": 1
           }
         ]
       }
     }
     ```

## Next Steps

With this plan approved, we can implement it in the code. We’ll:

1. Create the POCO classes for configuration.  
2. Implement the `ProxyManager`, `EndpointRateLimiterManager`, and supporting classes.  
3. Build the `ProxyRotationHandler` using `HttpClientHandler` or `HttpMessageInvoker` under the hood.  
4. Provide extension methods for easy registration in DI.  
5. Thoroughly test with an example project and sample requests.

Feel free to modify any aspect as your needs evolve.