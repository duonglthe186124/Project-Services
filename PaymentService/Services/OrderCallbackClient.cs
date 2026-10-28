using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PaymentService.Dtos;

namespace PaymentService.Services;

public interface IOrderCallbackClient
{
    Task<OrderPaymentCallbackResult> SendPaymentCallbackAsync(OrderPaymentCallbackPayload payload, CancellationToken ct = default);
}

public class OrderCallbackClient : IOrderCallbackClient
{
    public const string InternalApiKeyHeader = "X-Internal-Api-Key";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<OrderCallbackClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OrderCallbackClient(HttpClient httpClient, IConfiguration config, ILogger<OrderCallbackClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<OrderPaymentCallbackResult> SendPaymentCallbackAsync(OrderPaymentCallbackPayload payload, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/orders/internal/payment-callback")
            {
                Content = JsonContent.Create(payload)
            };

            var internalKey = _config["OrderService:InternalApiKey"];
            if (!string.IsNullOrWhiteSpace(internalKey))
            {
                request.Headers.Add(InternalApiKeyHeader, internalKey);
            }

            var response = await _httpClient.SendAsync(request, ct);
            var statusCode = (int)response.StatusCode;
            var rawBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawBody);
                    var root = doc.RootElement;
                    var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                    var isReplay = root.TryGetProperty("isReplay", out var r) && r.GetBoolean();
                    var refundRequired = root.TryGetProperty("refundRequired", out var rf) && rf.GetBoolean();
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "OK" : "OK";

                    return new OrderPaymentCallbackResult(true, statusCode, status, isReplay, refundRequired, msg);
                }
                catch
                {
                    return new OrderPaymentCallbackResult(true, statusCode, "ReadyForDispatch", false, false, "Đã gửi callback thành công.");
                }
            }

            string errorMessage = $"OrderService trả về HTTP {statusCode}";
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } msg)
                {
                    errorMessage = msg;
                }
            }
            catch
            {
                // Giữ nguyên errorMessage mặc định
            }

            _logger.LogWarning("Gọi payment-callback cho OrderId={OrderId} thất bại (HTTP {StatusCode}): {Message}",
                payload.OrderId, statusCode, errorMessage);

            return new OrderPaymentCallbackResult(false, statusCode, null, false, false, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi kết nối tới OrderService khi gửi payment-callback cho OrderId={OrderId}.", payload.OrderId);
            return new OrderPaymentCallbackResult(false, 503, null, false, false,
                $"Không thể kết nối tới OrderService: {ex.Message}");
        }
    }
}
