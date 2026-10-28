using System;
using System.Net.Http;
using System.Net.Http.Headers;
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
    Task<OrderSnapshotResult> GetOrderSnapshotAsync(string orderId, string? bearerToken, CancellationToken ct = default);

    Task<OrderPaymentCallbackResult> SendPaymentCallbackAsync(OrderPaymentCallbackPayload payload, CancellationToken ct = default);
}

public class OrderCallbackClient : IOrderCallbackClient
{
    public const string InternalApiKeyHeader = "X-Internal-Api-Key";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<OrderCallbackClient> _logger;

    public OrderCallbackClient(HttpClient httpClient, IConfiguration config, ILogger<OrderCallbackClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Lấy thông tin gốc của đơn hàng trực tiếp từ OrderService (Zero-Trust Pricing & Ownership).
    /// Chuyển tiếp Bearer JWT của khách sang OrderService để OrderAccessPolicy kiểm tra quyền sở hữu.
    /// </summary>
    public async Task<OrderSnapshotResult> GetOrderSnapshotAsync(string orderId, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/orders/{Uri.EscapeDataString(orderId)}");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            var response = await _httpClient.SendAsync(request, ct);
            var statusCode = (int)response.StatusCode;
            var rawBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(rawBody);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : orderId;
                var orderCode = root.TryGetProperty("orderCode", out var codeProp) ? codeProp.GetString() : null;
                var userId = root.TryGetProperty("userId", out var userProp) ? userProp.GetString() : null;
                var totalAmount = root.TryGetProperty("totalAmount", out var totalProp) ? totalProp.GetDecimal() : 0m;
                var orderStatus = root.TryGetProperty("orderStatus", out var statusProp) ? statusProp.GetString() : null;

                return new OrderSnapshotResult(true, statusCode, id, orderCode, userId, totalAmount, orderStatus, "OK");
            }

            string errorMessage = statusCode == 404
                ? "Không tìm thấy đơn hàng hoặc bạn không có quyền truy cập đơn hàng này."
                : $"OrderService trả về HTTP {statusCode}";

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

            return new OrderSnapshotResult(false, statusCode, null, null, null, 0m, null, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi kết nối tới OrderService khi kiểm tra đơn hàng OrderId={OrderId}.", orderId);
            return new OrderSnapshotResult(false, 503, null, null, null, 0m, null,
                $"Không thể kết nối tới OrderService để xác thực đơn hàng: {ex.Message}");
        }
    }

    public async Task<OrderPaymentCallbackResult> SendPaymentCallbackAsync(OrderPaymentCallbackPayload payload, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/orders/internal/payment-callback")
            {
                Content = JsonContent.Create(payload)
            };

            var internalKey = _config["OrderService:InternalApiKey"] ?? _config["InternalApi:Key"];
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
