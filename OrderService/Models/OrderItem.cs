using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Chi tiết từng đôi giày thể thao trong Đơn hàng chính thức.
/// Giá và thông tin sản phẩm được khóa chuẩn từ Catalog Stock Reservation Snapshot (Zero-Trust).
/// </summary>
public class OrderItem
{
    [BsonElement("productId")]
    public string ProductId { get; set; } = string.Empty;

    [BsonElement("productName")]
    public string ProductName { get; set; } = string.Empty;

    [BsonElement("category")]
    public string? Category { get; set; }

    [BsonElement("color")]
    public string? Color { get; set; }

    [BsonElement("size")]
    public int Size { get; set; }

    [BsonElement("quantity")]
    public int Quantity { get; set; }

    /// <summary>
    /// Đơn giá áp dụng chính thức tại thời điểm đặt hàng (lấy từ snapshot của Catalog).
    /// </summary>
    [BsonElement("unitPrice")]
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Tổng thành tiền dòng sản phẩm (UnitPrice * Quantity).
    /// </summary>
    [BsonElement("lineTotal")]
    public decimal LineTotal { get; set; }

    [BsonElement("thumbnailUrl")]
    public string ThumbnailUrl { get; set; } = string.Empty;
}
