using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderService.Dtos;

namespace OrderService.Services;

public class CatalogServiceClient : ICatalogServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CatalogServiceClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CatalogServiceClient(HttpClient httpClient, ILogger<CatalogServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<StockOperationResult> ReserveStockAsync(ReserveStockRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("Gửi yêu cầu ReserveStock tới CatalogService cho OrderId '{OrderId}' ({ItemCount} món)...",
            request.OrderId, request.Items.Count);

        var response = await _httpClient.PostAsJsonAsync("api/catalog/internal/stock/reserve", request, JsonOptions, ct);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<StockOperationResult>(JsonOptions, ct);
            return result ?? new StockOperationResult(true, "Reserved", "Khóa kho thành công", false);
        }

        // 409 Conflict (Hết hàng / SoldOut) hoặc 400 BadRequest (InvalidProductId) chứa StockOperationResult giải thích chi tiết
        if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                var errorResult = await response.Content.ReadFromJsonAsync<StockOperationResult>(JsonOptions, ct);
                if (errorResult != null)
                {
                    _logger.LogWarning("CatalogService từ chối khóa kho cho OrderId '{OrderId}' [HTTP {StatusCode}, Status: {Status}]: {Message}",
                        request.OrderId, (int)response.StatusCode, errorResult.Status, errorResult.Message);
                    return errorResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi deserialize phản hồi từ chối khóa kho từ CatalogService cho OrderId '{OrderId}'", request.OrderId);
            }
        }

        // Các mã lỗi 5xx hoặc không mong muốn -> Ném exception sau khi Polly retry thất bại
        response.EnsureSuccessStatusCode();
        return new StockOperationResult(false, "UnknownError", "Không thể xử lý phản hồi từ CatalogService", false);
    }

    /// <inheritdoc />
    public async Task<StockOperationResult> ConfirmStockAsync(ConfirmStockRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("Gửi yêu cầu ConfirmStock tới CatalogService cho OrderId '{OrderId}' (Reason: {Reason})...",
            request.OrderId, request.Reason);

        var response = await _httpClient.PostAsJsonAsync("api/catalog/internal/stock/confirm", request, JsonOptions, ct);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<StockOperationResult>(JsonOptions, ct);
            return result ?? new StockOperationResult(true, "Confirmed", "Xác nhận xuất kho thành công", false);
        }

        // 409 Conflict (Đơn đã Released/Expired) hoặc 404 NotFound
        if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                var errorResult = await response.Content.ReadFromJsonAsync<StockOperationResult>(JsonOptions, ct);
                if (errorResult != null)
                {
                    _logger.LogWarning("CatalogService từ chối confirm cho OrderId '{OrderId}' [HTTP {StatusCode}, Status: {Status}]: {Message}",
                        request.OrderId, (int)response.StatusCode, errorResult.Status, errorResult.Message);
                    return errorResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi deserialize phản hồi confirm từ CatalogService cho OrderId '{OrderId}'", request.OrderId);
            }
        }

        response.EnsureSuccessStatusCode();
        return new StockOperationResult(false, "UnknownError", "Không thể xử lý phản hồi từ CatalogService", false);
    }

    /// <inheritdoc />
    public async Task<StockOperationResult> ReleaseStockAsync(ReleaseStockRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("Gửi yêu cầu ReleaseStock tới CatalogService cho OrderId '{OrderId}' (Reason: {Reason})...",
            request.OrderId, request.Reason);

        var response = await _httpClient.PostAsJsonAsync("api/catalog/internal/stock/release", request, JsonOptions, ct);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<StockOperationResult>(JsonOptions, ct);
            return result ?? new StockOperationResult(true, "Released", "Nhả kho thành công", false);
        }

        if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                var errorResult = await response.Content.ReadFromJsonAsync<StockOperationResult>(JsonOptions, ct);
                if (errorResult != null)
                {
                    _logger.LogWarning("CatalogService phản hồi release cho OrderId '{OrderId}' [HTTP {StatusCode}, Status: {Status}]: {Message}",
                        request.OrderId, (int)response.StatusCode, errorResult.Status, errorResult.Message);
                    return errorResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi deserialize phản hồi release từ CatalogService cho OrderId '{OrderId}'", request.OrderId);
            }
        }

        response.EnsureSuccessStatusCode();
        return new StockOperationResult(false, "UnknownError", "Không thể xử lý phản hồi từ CatalogService", false);
    }

    /// <inheritdoc />
    public async Task<StockReservationDto?> GetReservationAsync(string orderId, CancellationToken ct = default)
    {
        _logger.LogInformation("Tra cứu phiên giữ hàng từ CatalogService cho OrderId '{OrderId}'...", orderId);

        var response = await _httpClient.GetAsync($"api/catalog/internal/stock/reservations/{Uri.EscapeDataString(orderId)}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StockReservationDto>(JsonOptions, ct);
    }
}
