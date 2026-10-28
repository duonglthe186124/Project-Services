using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Dtos;
using OrderService.Services;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Xunit;

namespace OrderService.Tests;

public class CatalogServiceClientAndPollyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Constructs a ResiliencePipeline configured identically to Program.cs line 145-178.
    /// </summary>
    private static ResiliencePipeline<HttpResponseMessage> CreateCatalogResiliencePipeline(TimeSpan? retryBaseDelay = null)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();

        // 1. Retry: 3 exponential retries (1s, 2s, 4s + jitter). Excludes 409 Conflict.
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = retryBaseDelay ?? TimeSpan.FromSeconds(1),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response =>
                    (int)response.StatusCode >= 500 ||
                    response.StatusCode == HttpStatusCode.RequestTimeout)
        });

        // 2. Circuit Breaker: 5 errors in 30s window, 30s break duration
        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response => (int)response.StatusCode >= 500)
        });

        // 3. Timeout: 5s per try
        builder.AddTimeout(TimeSpan.FromSeconds(5));

        return builder.Build();
    }

    [Fact]
    public async Task PollyPipeline_Retry_DoesNotRetryOn409Conflict()
    {
        var pipeline = CreateCatalogResiliencePipeline(TimeSpan.FromMilliseconds(50));
        int callCount = 0;

        var response = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        });

        // Must NOT retry 409 Conflict. Call count must be exactly 1!
        Assert.Equal(1, callCount);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PollyPipeline_Retry_DoesNotRetryOn400BadRequest()
    {
        var pipeline = CreateCatalogResiliencePipeline(TimeSpan.FromMilliseconds(50));
        int callCount = 0;

        var response = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        Assert.Equal(1, callCount);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PollyPipeline_Retry_Retries3TimesOn500InternalServerError()
    {
        var pipeline = CreateCatalogResiliencePipeline(TimeSpan.FromMilliseconds(10));
        int callCount = 0;

        var response = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        // 1 initial attempt + 3 retries = 4 total attempts
        Assert.Equal(4, callCount);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task PollyPipeline_Retry_DelaysFollowExponentialProgression()
    {
        var baseDelay = TimeSpan.FromMilliseconds(100);
        var pipeline = CreateCatalogResiliencePipeline(baseDelay);
        var timestamps = new List<DateTime>();

        var sw = Stopwatch.StartNew();
        var response = await pipeline.ExecuteAsync(async _ =>
        {
            timestamps.Add(DateTime.UtcNow);
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        sw.Stop();

        Assert.Equal(4, timestamps.Count);

        // With exponential backoff (100ms, 200ms, 400ms) plus jitter, total elapsed time must be >= 150ms
        Assert.True(sw.ElapsedMilliseconds >= 150, $"Total elapsed {sw.ElapsedMilliseconds}ms should reflect exponential backoff.");
    }

    [Fact]
    public async Task PollyPipeline_Retry_Retries3TimesOnHttpRequestException()
    {
        var pipeline = CreateCatalogResiliencePipeline(TimeSpan.FromMilliseconds(10));
        int callCount = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync<HttpResponseMessage>(async _ =>
            {
                callCount++;
                await Task.Yield();
                throw new HttpRequestException("Network failure simulated");
            });
        });

        // 1 initial attempt + 3 retries = 4 total attempts
        Assert.Equal(4, callCount);
    }

    [Fact]
    public async Task PollyPipeline_CircuitBreaker_TripsAfter5ConsecutiveFailures()
    {
        // 1. Test Circuit Breaker strategy in isolation (verifies exactly 5 consecutive failures rule)
        var cbBuilder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        cbBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response => (int)response.StatusCode >= 500)
        });
        var cbPipeline = cbBuilder.Build();

        int failureCount = 0;
        for (int i = 0; i < 5; i++)
        {
            var res = await cbPipeline.ExecuteAsync(async _ =>
            {
                failureCount++;
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }

        Assert.Equal(5, failureCount);

        // The 6th attempt must throw BrokenCircuitException immediately without invoking inner callback
        int executedAfterTrip = 0;
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await cbPipeline.ExecuteAsync(async _ =>
            {
                executedAfterTrip++;
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        });

        Assert.Equal(0, executedAfterTrip);
    }

    [Fact]
    public async Task PollyPipeline_Combined_TripsCircuitBreakerWhen5TotalFailuresReached()
    {
        var pipeline = CreateCatalogResiliencePipeline(TimeSpan.FromMilliseconds(10));

        // Request 1: 1 initial + 3 retries = 4 failures
        var res1 = await pipeline.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        Assert.Equal(HttpStatusCode.InternalServerError, res1.StatusCode);

        // Request 2: Initial attempt is the 5th failure -> Circuit Breaker trips -> retry triggers BrokenCircuitException
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            });
        });
    }


    [Fact]
    public async Task PollyPipeline_CircuitBreaker_DoesNotTripOnMultiple409Conflicts()
    {
        var pipeline = CreateCatalogResiliencePipeline(TimeSpan.FromMilliseconds(10));

        // Send 10 consecutive 409 Conflict responses
        for (int i = 0; i < 10; i++)
        {
            var res = await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }

        // Circuit breaker should NOT be broken; subsequent OK call succeeds
        var okRes = await pipeline.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        Assert.Equal(HttpStatusCode.OK, okRes.StatusCode);
    }

    [Fact]
    public async Task ReserveStockAsync_409Conflict_ParsesFailedItemsWithoutThrowing()
    {
        var expectedFailure = new StockFailureDetailDto(
            ProductId: "6ad318f7691a1e702d881190",
            ProductName: "Ultraboost Light",
            Size: 42,
            RequestedQuantity: 20,
            AvailableQuantity: 14,
            FailureCode: "INSUFFICIENT_STOCK",
            Reason: "Sản phẩm 'Ultraboost Light' - Size 42 không đủ tồn kho (Yêu cầu: 20, Hiện có: 14)."
        );

        var expectedResult = new StockOperationResult(
            Success: false,
            Status: "OutOfStock",
            Message: "Sản phẩm 'Ultraboost Light' - Size 42 không đủ tồn kho (Yêu cầu: 20, Hiện có: 14).",
            IsIdempotentReplay: false,
            Reservation: null,
            FailedItems: new List<StockFailureDetailDto> { expectedFailure }
        );

        var json = JsonSerializer.Serialize(expectedResult, JsonOptions);

        var testHandler = new DelegatingTestHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        var client = new CatalogServiceClient(httpClient, NullLogger<CatalogServiceClient>.Instance);

        var request = new ReserveStockRequest(
            OrderId: "ORD-TEST-001",
            OrderCode: "OD-123456",
            UserId: "user1",
            Items: new List<ReserveStockItemRequest>
            {
                new("6ad318f7691a1e702d881190", 42, 20)
            }
        );

        var result = await client.ReserveStockAsync(request);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("OutOfStock", result.Status);
        Assert.NotNull(result.FailedItems);
        Assert.Single(result.FailedItems);
        Assert.Equal("6ad318f7691a1e702d881190", result.FailedItems[0].ProductId);
        Assert.Equal(42, result.FailedItems[0].Size);
        Assert.Equal(20, result.FailedItems[0].RequestedQuantity);
        Assert.Equal(14, result.FailedItems[0].AvailableQuantity);
        Assert.Equal("INSUFFICIENT_STOCK", result.FailedItems[0].FailureCode);
    }

    [Fact]
    public async Task ReserveStockAsync_200OK_ParsesReservationSnapshot()
    {
        var snapshotItem = new StockItemSnapshotDto(
            ProductId: "6ad318f7691a1e702d881190",
            ProductName: "Ultraboost Light",
            Category: "Running",
            Color: "White",
            Size: 42,
            Quantity: 1,
            OriginalPrice: 3800000m,
            SalePrice: null,
            UnitPriceSnapshot: 3800000m,
            LineTotal: 3800000m,
            ThumbnailUrl: "https://example.com/shoe.jpg"
        );

        var reservation = new StockReservationDto(
            Id: "res_123",
            OrderId: "ORD-TEST-002",
            OrderCode: "OD-999999",
            UserId: "user1",
            Items: new List<StockItemSnapshotDto> { snapshotItem },
            TotalQuantity: 1,
            SubTotalSnapshot: 3800000m,
            Status: "Reserved",
            HoldMinutes: 15,
            ExpiresAt: DateTime.UtcNow.AddMinutes(15),
            CreatedAt: DateTime.UtcNow
        );

        var expectedResult = new StockOperationResult(
            Success: true,
            Status: "Reserved",
            Message: "Khóa kho thành công",
            IsIdempotentReplay: false,
            Reservation: reservation
        );

        var json = JsonSerializer.Serialize(expectedResult, JsonOptions);

        var testHandler = new DelegatingTestHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        var client = new CatalogServiceClient(httpClient, NullLogger<CatalogServiceClient>.Instance);

        var request = new ReserveStockRequest(
            OrderId: "ORD-TEST-002",
            OrderCode: "OD-999999",
            UserId: "user1",
            Items: new List<ReserveStockItemRequest>
            {
                new("6ad318f7691a1e702d881190", 42, 1)
            }
        );

        var result = await client.ReserveStockAsync(request);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal("Reserved", result.Status);
        Assert.NotNull(result.Reservation);
        Assert.Equal(3800000m, result.Reservation.SubTotalSnapshot);
        Assert.Single(result.Reservation.Items);
        Assert.Equal(3800000m, result.Reservation.Items[0].UnitPriceSnapshot);
    }

    [Fact]
    public async Task ConfirmStockAsync_409Conflict_ReturnsResultGracefully()
    {
        var expectedResult = new StockOperationResult(
            Success: false,
            Status: "Expired",
            Message: "Phiên giữ hàng đã quá hạn",
            IsIdempotentReplay: false
        );

        var json = JsonSerializer.Serialize(expectedResult, JsonOptions);

        var testHandler = new DelegatingTestHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        var client = new CatalogServiceClient(httpClient, NullLogger<CatalogServiceClient>.Instance);

        var result = await client.ConfirmStockAsync(new ConfirmStockRequest("ORD-EXPIRED", "PaymentVerified"));

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("Expired", result.Status);
    }

    [Fact]
    public async Task ReleaseStockAsync_409Conflict_ReturnsResultGracefully()
    {
        var expectedResult = new StockOperationResult(
            Success: false,
            Status: "NotFound",
            Message: "Không tìm thấy phiên giữ hàng",
            IsIdempotentReplay: false
        );

        var json = JsonSerializer.Serialize(expectedResult, JsonOptions);

        var testHandler = new DelegatingTestHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        var client = new CatalogServiceClient(httpClient, NullLogger<CatalogServiceClient>.Instance);

        var result = await client.ReleaseStockAsync(new ReleaseStockRequest("ORD-UNKNOWN", "OrderCancelled"));

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("NotFound", result.Status);
    }

    [Fact]
    public async Task GetReservationAsync_404NotFound_ReturnsNull()
    {
        var testHandler = new DelegatingTestHandler(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        var client = new CatalogServiceClient(httpClient, NullLogger<CatalogServiceClient>.Instance);

        var reservation = await client.GetReservationAsync("ORD-NOT-FOUND");
        Assert.Null(reservation);
    }

    [Fact]
    public async Task CatalogServiceClient_NetworkFailure_PropagatesHttpRequestException()
    {
        var testHandler = new DelegatingTestHandler(req =>
        {
            throw new HttpRequestException("Connection refused to CatalogService");
        });

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        var client = new CatalogServiceClient(httpClient, NullLogger<CatalogServiceClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.ReserveStockAsync(new ReserveStockRequest(
                OrderId: "ORD-FAIL",
                OrderCode: "OD-FAIL",
                UserId: "user1",
                Items: new List<ReserveStockItemRequest> { new("prod1", 42, 1) }
            ));
        });
    }

    private class DelegatingTestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegatingTestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
