using System.Threading;
using System.Threading.Tasks;
using OrderService.Dtos;

namespace OrderService.Services;

/// <summary>
/// Client HTTP chịu lỗi (Resilient Client) giao tiếp với CatalogService.
/// Bọc các chính sách Polly: Retry lũy thừa 3 lần (1s, 2s, 4s + Jitter), Circuit Breaker (5 lỗi / 30s),
/// và loại trừ không retry mã HTTP 409 Conflict.
/// </summary>
public interface ICatalogServiceClient
{
    /// <summary>
    /// [Saga Step 1] Tạm khóa tồn kho nguyên tử theo OrderId + Size + Số lượng (TTL mặc định 15 phút).
    /// Trả về StockOperationResult với Success = false khi hết hàng (HTTP 409) kèm FailedItems.
    /// </summary>
    Task<StockOperationResult> ReserveStockAsync(ReserveStockRequest request, CancellationToken ct = default);

    /// <summary>
    /// [Saga Step 2a] Xác nhận xuất kho chính thức sau khi thanh toán thành công hoặc duyệt đơn.
    /// Trả về StockOperationResult với Success = false khi đơn đã bị hủy/quá hạn (HTTP 409).
    /// </summary>
    Task<StockOperationResult> ConfirmStockAsync(ConfirmStockRequest request, CancellationToken ct = default);

    /// <summary>
    /// [Saga Step 2b / Compensation] Nhả kho bù trừ cộng ngược lại vào kệ khi đơn hủy, timeout hoặc lỗi thanh toán.
    /// Idempotent 100%: gọi lại nhiều lần không cộng thừa kho.
    /// </summary>
    Task<StockOperationResult> ReleaseStockAsync(ReleaseStockRequest request, CancellationToken ct = default);

    /// <summary>
    /// Tra cứu chi tiết phiên giữ hàng theo OrderId. Trả về null nếu không tìm thấy (HTTP 404).
    /// </summary>
    Task<StockReservationDto?> GetReservationAsync(string orderId, CancellationToken ct = default);
}
