using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CatalogService.Models;

/// <summary>
/// Chuẩn hóa trạng thái của phiên tạm giữ tồn kho (Stock Reservation Lifecycle).
/// </summary>
public static class StockReservationStatus
{
    /// <summary>Đang tạm khóa tồn kho, đếm ngược TTL (mặc định 15 phút) chờ khách thanh toán.</summary>
    public const string Reserved = "Reserved";

    /// <summary>Đã xác nhận xuất kho chính thức sau khi thanh toán thành công hoặc duyệt đơn COD.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>Đã nhả tồn kho cộng ngược lại vào kệ (do khách hủy đơn, thanh toán lỗi hoặc hoàn hàng RTO).</summary>
    public const string Released = "Released";

    /// <summary>Quá hạn TTL 15 phút chưa thanh toán, đã được Background Worker tự động thu hồi tồn kho.</summary>
    public const string Expired = "Expired";
}

/// <summary>
/// Bản ghi quản lý phiên khóa tồn kho tạm thời theo từng Đơn hàng (OrderId).
/// Lưu trong Collection "StockReservations" của CatalogDb với Unique Index trên OrderId để đảm bảo tính Lũy đẳng (Idempotency).
/// </summary>
public class StockReservation
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// Mã định danh đơn hàng (Unique Index ux_orderId).
    /// Đảm bảo dù OrderService gọi retry nhiều lần cho cùng 1 OrderId thì cũng chỉ khóa kho đúng 1 lần duy nhất.
    /// </summary>
    [BsonElement("orderId")]
    public string OrderId { get; set; } = string.Empty;

    [BsonElement("orderCode")]
    public string OrderCode { get; set; } = string.Empty;

    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("items")]
    public List<ReservedStockItem> Items { get; set; } = new();

    /// <summary>
    /// Tổng số đôi giày trong phiên khóa kho này.
    /// </summary>
    [BsonElement("totalQuantity")]
    public int TotalQuantity => Items?.Sum(i => i.Quantity) ?? 0;

    /// <summary>
    /// Tổng giá trị tiền hàng (VNĐ) tính theo giá chuẩn tại thời điểm khóa kho (Zero-Trust Pricing Snapshot).
    /// </summary>
    [BsonElement("subTotalSnapshot")]
    public decimal SubTotalSnapshot => Items?.Sum(i => i.LineTotal) ?? 0m;

    /// <summary>
    /// Trạng thái: Reserved, Confirmed, Released, Expired
    /// </summary>
    [BsonElement("status")]
    public string Status { get; set; } = StockReservationStatus.Reserved;

    /// <summary>
    /// Thời gian giữ hàng (phút), mặc định 15 phút.
    /// </summary>
    [BsonElement("holdMinutes")]
    public int HoldMinutes { get; set; } = 15;

    /// <summary>
    /// Thời điểm hết hạn giữ hàng (UTC). Quá mốc này mà Status vẫn là Reserved thì Worker sẽ tự động nhả kho.
    /// </summary>
    [BsonElement("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    [BsonElement("confirmedAt")]
    public DateTime? ConfirmedAt { get; set; }

    [BsonElement("releasedAt")]
    public DateTime? ReleasedAt { get; set; }

    [BsonElement("releaseReason")]
    public string? ReleaseReason { get; set; }
}

/// <summary>
/// Chi tiết từng đôi giày + size được khóa kho kèm Snapshot giá gốc từ CatalogDb (chống giả mạo giá từ Client).
/// </summary>
public class ReservedStockItem
{
    [BsonElement("productId")]
    public string ProductId { get; set; } = string.Empty;

    [BsonElement("productName")]
    public string ProductName { get; set; } = string.Empty;

    [BsonElement("category")]
    public string Category { get; set; } = string.Empty;

    [BsonElement("color")]
    public string Color { get; set; } = string.Empty;

    [BsonElement("size")]
    public int Size { get; set; }

    [BsonElement("quantity")]
    public int Quantity { get; set; }

    [BsonElement("originalPrice")]
    public decimal OriginalPrice { get; set; }

    [BsonElement("salePrice")]
    public decimal? SalePrice { get; set; }

    /// <summary>
    /// Đơn giá áp dụng thực tế tại thời điểm khóa kho (= SalePrice ?? OriginalPrice).
    /// </summary>
    [BsonElement("unitPriceSnapshot")]
    public decimal UnitPriceSnapshot { get; set; }

    [BsonElement("lineTotal")]
    public decimal LineTotal => UnitPriceSnapshot * Quantity;

    [BsonElement("thumbnailUrl")]
    public string ThumbnailUrl { get; set; } = string.Empty;
}
