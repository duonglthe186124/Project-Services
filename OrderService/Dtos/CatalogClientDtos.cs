using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace OrderService.Dtos;

/// <summary>
/// Yêu cầu khóa kho cho từng mặt hàng (kích cỡ và số lượng).
/// </summary>
public record ReserveStockItemRequest(
    [Required(ErrorMessage = "ProductId là bắt buộc.")]
    string ProductId,

    [Range(30, 55, ErrorMessage = "Size giày phải từ 30 đến 55.")]
    int Size,

    [Range(1, 100, ErrorMessage = "Số lượng đặt giữ cho mỗi size phải từ 1 đến 100.")]
    int Quantity);

/// <summary>
/// Yêu cầu khóa kho nguyên tử theo Đơn hàng gửi tới CatalogService.
/// </summary>
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

/// <summary>
/// Yêu cầu xác nhận xuất kho chính thức sau khi thanh toán hoặc duyệt COD.
/// </summary>
public record ConfirmStockRequest(
    [Required(ErrorMessage = "OrderId là bắt buộc.")]
    string OrderId,

    string? Reason = "PaymentVerified");

/// <summary>
/// Yêu cầu nhả kho hoàn trả tồn kho về kệ (hủy đơn, timeout, lỗi thanh toán).
/// </summary>
public record ReleaseStockRequest(
    [Required(ErrorMessage = "OrderId là bắt buộc.")]
    string OrderId,

    string Reason = "OrderCancelledOrExpired");

/// <summary>
/// Chi tiết lỗi khi không đủ tồn kho hoặc sản phẩm không khả dụng.
/// </summary>
public record StockFailureDetailDto(
    string ProductId,
    string? ProductName,
    int Size,
    int RequestedQuantity,
    int AvailableQuantity,
    string FailureCode,
    string Reason);

/// <summary>
/// Snapshot giá và thông tin sản phẩm tại thời điểm khóa kho (Zero-Trust Pricing).
/// </summary>
public record StockItemSnapshotDto(
    string ProductId,
    string ProductName,
    string? Category,
    string? Color,
    int Size,
    int Quantity,
    decimal OriginalPrice,
    decimal? SalePrice,
    decimal UnitPriceSnapshot,
    decimal LineTotal,
    string? ThumbnailUrl);

/// <summary>
/// Thông tin chi tiết phiên giữ hàng (StockReservation) từ CatalogService.
/// </summary>
public record StockReservationDto(
    string? Id,
    string OrderId,
    string? OrderCode,
    string? UserId,
    List<StockItemSnapshotDto> Items,
    int TotalQuantity,
    decimal SubTotalSnapshot,
    string Status,
    int HoldMinutes,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt = null,
    DateTime? ConfirmedAt = null,
    DateTime? ReleasedAt = null,
    string? ReleaseReason = null);

/// <summary>
/// Kết quả phản hồi chuẩn từ các API quản lý tồn kho của CatalogService.
/// </summary>
public record StockOperationResult(
    bool Success,
    string Status,
    string Message,
    bool IsIdempotentReplay,
    StockReservationDto? Reservation = null,
    List<StockFailureDetailDto>? FailedItems = null);
