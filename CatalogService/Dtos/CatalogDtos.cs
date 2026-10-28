using System.ComponentModel.DataAnnotations;
using CatalogService.Models;

namespace CatalogService.Dtos;

public class ProductFilterQuery
{
    public string? Keyword { get; set; }
    public string? Category { get; set; }
    public string? Gender { get; set; }
    public string? Color { get; set; }
    public int? Size { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool? OnSale { get; set; }
    public string? Tag { get; set; }
    
    /// <summary>
    /// Lọc theo trạng thái: null = mặc định chỉ lấy sản phẩm đang kinh doanh (true), false = đã ẩn
    /// </summary>
    public bool? IsActive { get; set; }

    // newest, price_asc, price_desc, rating
    public string? SortBy { get; set; } = "newest";

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 12;
}

public record PagedResult<T>(
    List<T> Items,
    long TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public record CreateProductRequest(
    [Required(ErrorMessage = "Tên giày là bắt buộc.")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "Tên giày từ 2 đến 200 ký tự.")]
    string Name,

    [Required(ErrorMessage = "Dòng giày (Category) là bắt buộc.")]
    string Category,

    string? Gender,

    string Description,

    [Range(0, 1000000000, ErrorMessage = "Giá sản phẩm không hợp lệ.")]
    decimal Price,

    decimal? SalePrice,

    [Required(ErrorMessage = "Màu sắc là bắt buộc.")]
    string Color,

    List<SizeStockDto>? Sizes,

    [Required(ErrorMessage = "Ảnh đại diện chính (Thumbnail) là bắt buộc.")]
    string ThumbnailUrl,

    List<string>? ImageUrls,

    List<string>? Tags,

    bool IsActive = true);

public record UpdateProductRequest(
    [Required(ErrorMessage = "Tên giày là bắt buộc.")]
    string Name,

    [Required(ErrorMessage = "Dòng giày (Category) là bắt buộc.")]
    string Category,

    string? Gender,

    string Description,

    [Range(0, 1000000000, ErrorMessage = "Giá sản phẩm không hợp lệ.")]
    decimal Price,

    decimal? SalePrice,

    string Color,

    List<SizeStockDto>? Sizes,

    string ThumbnailUrl,

    List<string>? ImageUrls,

    List<string>? Tags,

    bool IsActive = true);

public record SizeStockDto(
    [Range(30, 55, ErrorMessage = "Size giày phải từ 30 đến 55.")]
    int Size,

    [Range(0, 10000, ErrorMessage = "Số lượng tồn không hợp lệ.")]
    int Quantity);

public record AddReviewRequest(
    [Range(1, 5, ErrorMessage = "Đánh giá từ 1 đến 5 sao.")]
    int Rating,

    [Required(ErrorMessage = "Nội dung nhận xét là bắt buộc.")]
    [StringLength(1000, MinimumLength = 3, ErrorMessage = "Nhận xét từ 3 đến 1000 ký tự.")]
    string Comment);

public record PriceRangeDto(decimal Min, decimal Max);

public record CatalogFiltersResponse(
    List<string> Categories,
    List<string> Genders,
    List<string> Colors,
    List<int> AvailableSizes,
    List<string> Tags,
    PriceRangeDto PriceRange);

// ===================== SAGA / INTERNAL STOCK RESERVATION DTOs =====================

public record ReserveStockItemRequest(
    [Required(ErrorMessage = "ProductId là bắt buộc.")]
    string ProductId,

    [Range(30, 55, ErrorMessage = "Size giày phải từ 30 đến 55.")]
    int Size,

    [Range(1, 100, ErrorMessage = "Số lượng đặt giữ cho mỗi size phải từ 1 đến 100.")]
    int Quantity);

public record ReserveStockRequest(
    [Required(ErrorMessage = "OrderId là bắt buộc để đảm bảo tính Lũy đẳng (Idempotency).")]
    string OrderId,

    string? OrderCode,

    string? UserId,

    [Required(ErrorMessage = "Danh sách sản phẩm cần khóa kho không được để trống.")]
    [MinLength(1, ErrorMessage = "Đơn hàng phải có ít nhất 1 sản phẩm.")]
    List<ReserveStockItemRequest> Items,

    [Range(1, 1440, ErrorMessage = "Thời gian giữ hàng (HoldMinutes) phải từ 1 đến 1440 phút.")]
    int HoldMinutes = 15);

public record ConfirmStockRequest(
    [Required(ErrorMessage = "OrderId là bắt buộc.")]
    string OrderId,

    string? Reason = "PaymentVerified");

public record ReleaseStockRequest(
    [Required(ErrorMessage = "OrderId là bắt buộc.")]
    string OrderId,

    string Reason = "OrderCancelledOrExpired");

public record StockFailureDetail(
    string ProductId,
    string? ProductName,
    int Size,
    int RequestedQuantity,
    int AvailableQuantity,
    string FailureCode,
    string Reason);

public record StockOperationResult(
    bool Success,
    string Status,
    string Message,
    bool IsIdempotentReplay,
    StockReservation? Reservation = null,
    List<StockFailureDetail>? FailedItems = null);

