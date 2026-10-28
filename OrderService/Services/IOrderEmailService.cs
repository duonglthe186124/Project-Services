using System.Threading;
using System.Threading.Tasks;
using OrderService.Models;

namespace OrderService.Services;

/// <summary>
/// Gửi email xác nhận đơn hàng (chi tiết đơn, mã vận đơn, địa chỉ, SĐT...).
/// Hợp đồng: KHÔNG BAO GIỜ ném exception — lỗi SMTP/thiếu cấu hình chỉ được log, luồng đơn hàng vẫn thành công.
/// </summary>
public interface IOrderEmailService
{
    /// <returns>true nếu SMTP gửi thành công; false nếu bỏ qua/lỗi.</returns>
    Task<bool> SendOrderConfirmationAsync(Order order, CancellationToken ct = default);
}
