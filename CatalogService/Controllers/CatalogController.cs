using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CatalogService.Dtos;
using CatalogService.Models;
using CatalogService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CatalogService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CatalogController : ControllerBase
{
    private readonly ICatalogService _catalogService;

    public CatalogController(ICatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    /// <summary>
    /// Tra cứu & Lọc danh mục giày Adidas (Công khai)
    /// Hỗ trợ tìm theo tên, lọc theo category, gender, color, size còn hàng, khoảng giá, sale, sắp xếp và phân trang.
    /// </summary>
    [HttpGet("products")]
    public async Task<IActionResult> GetProducts([FromQuery] ProductFilterQuery query)
    {
        var result = await _catalogService.GetProductsAsync(query);
        return Ok(result);
    }

    /// <summary>
    /// Xem chi tiết 1 mẫu giày Adidas (Công khai, hỗ trợ Admin xem sản phẩm bị ẩn qua includeInactive=true)
    /// </summary>
    [HttpGet("products/{id}")]
    public async Task<IActionResult> GetProductById(string id, [FromQuery] bool includeInactive = false)
    {
        var product = await _catalogService.GetProductByIdAsync(id, includeInactive);
        if (product == null)
            return NotFound(new { message = "Không tìm thấy sản phẩm hoặc sản phẩm đã ngừng kinh doanh." });

        return Ok(product);
    }

    /// <summary>
    /// Lấy danh sách metadata bộ lọc (Categories, Colors, Sizes còn hàng, khoảng giá Min/Max) để Web vẽ thanh Filter bên trái.
    /// </summary>
    [HttpGet("filters")]
    public async Task<IActionResult> GetFilters()
    {
        var filters = await _catalogService.GetFiltersAsync();
        return Ok(filters);
    }

    /// <summary>
    /// Gửi đánh giá sao & nhận xét cho đôi giày (Yêu cầu đăng nhập Customer hoặc Admin)
    /// </summary>
    [Authorize]
    [HttpPost("products/{id}/reviews")]
    public async Task<IActionResult> AddReview(string id, [FromBody] AddReviewRequest req)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var fullName = User.FindFirstValue("fullName") ?? User.FindFirstValue(ClaimTypes.Name) ?? "Khách hàng";

        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Token không hợp lệ. Vui lòng đăng nhập lại." });

        var updatedProduct = await _catalogService.AddReviewAsync(id, userId, fullName, req);
        if (updatedProduct == null)
            return NotFound(new { message = "Không tìm thấy sản phẩm." });

        return Ok(new
        {
            message = "Gửi đánh giá thành công! Cảm ơn bạn đã phản hồi.",
            averageRating = updatedProduct.AverageRating,
            reviewCount = updatedProduct.ReviewCount,
            reviews = updatedProduct.Reviews
        });
    }

    /// <summary>
    /// Thêm mới mẫu giày Adidas vào kho (Chỉ dành cho Admin)
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPost("products")]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest req)
    {
        var created = await _catalogService.CreateProductAsync(req);
        return CreatedAtAction(nameof(GetProductById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Cập nhật thông tin giày, giá, tồn kho theo size (Chỉ dành cho Admin)
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPut("products/{id}")]
    public async Task<IActionResult> UpdateProduct(string id, [FromBody] UpdateProductRequest req)
    {
        var updated = await _catalogService.UpdateProductAsync(id, req);
        if (updated == null)
            return NotFound(new { message = "Không tìm thấy sản phẩm để cập nhật." });

        return Ok(updated);
    }

    /// <summary>
    /// Xóa mẫu giày khỏi cửa hàng (Mặc định: Xóa mềm - ẩn sản phẩm. Truyền hardDelete=true để xóa vĩnh viễn khỏi Database - Chỉ dành cho Admin)
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpDelete("products/{id}")]
    public async Task<IActionResult> DeleteProduct(string id, [FromQuery] bool hardDelete = false)
    {
        var success = await _catalogService.DeleteProductAsync(id, hardDelete);
        if (!success)
            return NotFound(new { message = "Không tìm thấy sản phẩm để xóa." });

        var msg = hardDelete
            ? "Đã xóa vĩnh viễn sản phẩm khỏi cơ sở dữ liệu thành công."
            : "Đã ẩn sản phẩm khỏi cửa hàng thành công.";

        return Ok(new { message = msg, hardDeleted = hardDelete });
    }

    /// <summary>
    /// Khôi phục mẫu giày đã bị ẩn/xóa mềm về lại cửa hàng (Chỉ dành cho Admin)
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPatch("products/{id}/restore")]
    public async Task<IActionResult> RestoreProduct(string id)
    {
        var success = await _catalogService.RestoreProductAsync(id);
        if (!success)
            return NotFound(new { message = "Không tìm thấy sản phẩm để khôi phục." });

        return Ok(new { message = "Đã khôi phục sản phẩm mở bán trở lại thành công." });
    }

    // ===================== SAGA / INTERNAL STOCK RESERVATION ENDPOINTS =====================

    /// <summary>
    /// [Saga Step 1] Tạm khóa tồn kho nguyên tử (Atomic Stock Reservation) theo OrderId + Size + Số lượng (TTL mặc định 15 phút).
    /// Chống bán lố (Overselling F-01), hỗ trợ Idempotency theo OrderId, tự động Rollback nếu 1 đôi trong giỏ hết hàng, và trả về Snapshot giá chuẩn.
    /// </summary>
    [HttpPost("internal/stock/reserve")]
    public async Task<IActionResult> ReserveStock([FromBody] ReserveStockRequest req)
    {
        var result = await _catalogService.ReserveStockAsync(req);
        if (result.Success)
            return Ok(result);

        if (result.Status == "InvalidProductId")
            return BadRequest(result);

        return Conflict(result);
    }

    /// <summary>
    /// [Saga Step 2a] Xác nhận xuất kho chính thức (Confirm Stock Deduction) sau khi khách thanh toán thành công hoặc duyệt đơn COD.
    /// </summary>
    [HttpPost("internal/stock/confirm")]
    public async Task<IActionResult> ConfirmStock([FromBody] ConfirmStockRequest req)
    {
        var result = await _catalogService.ConfirmStockAsync(req);
        if (result.Success)
            return Ok(result);

        if (result.Status == "NotFound")
            return NotFound(result);

        return Conflict(result);
    }

    /// <summary>
    /// [Saga Step 2b / Compensation] Nhả tồn kho (Release Stock) cộng ngược lại vào kệ khi đơn hủy, lỗi thanh toán, quá hạn 15 phút hoặc hoàn hàng RTO.
    /// Đảm bảo tính Lũy đẳng (Idempotent): Gọi nhiều lần tuyệt đối không cộng thừa tồn kho.
    /// </summary>
    [HttpPost("internal/stock/release")]
    public async Task<IActionResult> ReleaseStock([FromBody] ReleaseStockRequest req)
    {
        var result = await _catalogService.ReleaseStockAsync(req);
        if (result.Success)
            return Ok(result);

        if (result.Status == "NotFound")
            return NotFound(result);

        return Conflict(result);
    }

    /// <summary>
    /// Tra cứu chi tiết phiên giữ hàng (StockReservation) theo OrderId.
    /// </summary>
    [HttpGet("internal/stock/reservations/{orderId}")]
    public async Task<IActionResult> GetReservationByOrderId(string orderId)
    {
        var reservation = await _catalogService.GetReservationByOrderIdAsync(orderId);
        if (reservation == null)
            return NotFound(new { message = $"Không tìm thấy phiên giữ hàng cho OrderId '{orderId}'." });

        return Ok(reservation);
    }

    /// <summary>
    /// Xem danh sách các phiên giữ hàng trong kho (Hỗ trợ lọc theo status: Reserved, Confirmed, Released, Expired).
    /// </summary>
    [HttpGet("internal/stock/reservations")]
    public async Task<IActionResult> GetReservations([FromQuery] string? status = null, [FromQuery] int limit = 50)
    {
        var reservations = await _catalogService.GetReservationsAsync(status, limit);
        return Ok(new
        {
            totalCount = reservations.Count,
            statusFilter = status,
            items = reservations
        });
    }

    /// <summary>
    /// Kích hoạt quét và thu hồi ngay lập tức các phiên giữ hàng đã quá hạn TTL (Phục vụ kiểm thử nhanh trên Swagger).
    /// </summary>
    [HttpPost("internal/stock/cleanup-expired")]
    public async Task<IActionResult> CleanupExpiredReservations(CancellationToken cancellationToken)
    {
        var releasedCount = await _catalogService.ReleaseExpiredReservationsAsync(cancellationToken);
        return Ok(new
        {
            message = $"Đã quét và thu hồi tồn kho cho {releasedCount} phiên giữ hàng quá hạn.",
            releasedCount,
            sweptAtUtc = DateTime.UtcNow
        });
    }
}

