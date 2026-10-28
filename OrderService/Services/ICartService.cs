using System.Threading;
using System.Threading.Tasks;
using OrderService.Dtos;
using OrderService.Models;

namespace OrderService.Services;

/// <summary>
/// Giao diện xử lý nghiệp vụ Giỏ hàng (Cart) trong OrderService.
/// Thực thi toàn bộ thao tác thêm, cập nhật, xóa nguyên tử bằng MongoDB FindOneAndUpdateAsync.
/// </summary>
public interface ICartService
{
    /// <summary>
    /// Lấy giỏ hàng đang hoạt động của người dùng.
    /// Nếu người dùng chưa có giỏ hàng, tự động khởi tạo giỏ hàng rỗng và trả về.
    /// </summary>
    Task<Cart> GetOrCreateCartAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Thêm sản phẩm vào giỏ hàng:
    /// - Nếu cặp (ProductId, Size) đã tồn tại: tăng nguyên tử số lượng ($inc: items.$.quantity).
    /// - Nếu chưa tồn tại: tra cứu snapshot từ CatalogService và thêm mục mới ($push: items).
    /// </summary>
    Task<Cart> AddItemToCartAsync(string userId, AddToCartRequest request, CancellationToken ct = default);

    /// <summary>
    /// Cập nhật số lượng của một mặt hàng cụ thể (ProductId, Size) trong giỏ:
    /// - Nếu quantity > 0: cập nhật nguyên tử ($set: items.$.quantity).
    /// - Nếu quantity <= 0: tự động xóa mặt hàng ($pull: items).
    /// - Nếu không tìm thấy mặt hàng: trả về null.
    /// </summary>
    Task<Cart?> UpdateItemQuantityAsync(string userId, string productId, int size, int quantity, CancellationToken ct = default);

    /// <summary>
    /// Xóa một mặt hàng cụ thể (ProductId, Size) khỏi giỏ hàng ($pull: items).
    /// - Nếu không tìm thấy mặt hàng trong giỏ: trả về null.
    /// </summary>
    Task<Cart?> RemoveItemFromCartAsync(string userId, string productId, int size, CancellationToken ct = default);

    /// <summary>
    /// Làm trống toàn bộ giỏ hàng của người dùng ($set: items = []).
    /// </summary>
    Task<Cart> ClearCartAsync(string userId, CancellationToken ct = default);
}
