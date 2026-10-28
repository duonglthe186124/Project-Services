using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Quản lý giỏ hàng mua sắm của khách hàng trước khi tiến hành thanh toán (Checkout).
/// Mỗi khách hàng chỉ có duy nhất 1 giỏ hàng trong Collection "Carts" (Unique Index ux_userId).
/// </summary>
public class Cart
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// Mã định danh người dùng (Unique Index ux_userId).
    /// </summary>
    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Danh sách sản phẩm trong giỏ hàng.
    /// </summary>
    [BsonElement("items")]
    public List<CartItem> Items { get; set; } = new();

    /// <summary>
    /// Thời điểm khởi tạo giỏ hàng (UTC).
    /// </summary>
    [BsonElement("createdAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Thời điểm cập nhật giỏ hàng gần nhất (UTC).
    /// </summary>
    [BsonElement("updatedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tổng số lượng mặt hàng trong giỏ (tính tổng từ Quantity của từng mục).
    /// </summary>
    [BsonIgnore]
    public int TotalItems => Items?.Sum(i => i.Quantity) ?? 0;

    /// <summary>
    /// Tạm tính giá trị giỏ hàng tại thời điểm xem giỏ.
    /// Lưu ý: Giá chính thức cuối cùng khi thanh toán sẽ được khóa từ snapshot CatalogService (Zero-Trust).
    /// </summary>
    [BsonIgnore]
    public decimal SubTotal => Items?.Sum(i => i.LineTotal) ?? 0m;
}
