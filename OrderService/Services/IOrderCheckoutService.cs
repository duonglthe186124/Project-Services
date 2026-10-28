using System.Threading;
using System.Threading.Tasks;
using OrderService.Dtos;

namespace OrderService.Services;

/// <summary>
/// Tạo đơn hàng từ giỏ hàng (hoặc danh sách mua ngay): khóa kho Catalog 15 phút → tạo Order + OrderSagaState
/// ở trạng thái AwaitingPayment → dọn giỏ hàng. KHÔNG chọn phương thức thanh toán, KHÔNG gửi email
/// (email chỉ gửi khi PaymentService báo thanh toán/COD thành công).
/// </summary>
public interface IOrderCheckoutService
{
    /// <param name="userId">Id người dùng từ JWT (sub).</param>
    /// <param name="tokenEmail">Claim "email" của JWT (dùng khi body không ghi đè).</param>
    /// <param name="tokenFullName">Claim "fullName" của JWT (dùng khi body không có CustomerName).</param>
    Task<OrderOperationResult<CheckoutResponse>> CheckoutAsync(
        string userId, string? tokenEmail, string? tokenFullName, CheckoutRequest request, CancellationToken ct = default);
}
