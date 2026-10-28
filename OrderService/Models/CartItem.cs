using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Chi tiết một mục sản phẩm (giày thể thao) nằm trong giỏ hàng của khách hàng.
/// Mỗi cặp (ProductId, Size) là duy nhất trong một giỏ hàng.
/// </summary>
public class CartItem
{
    [BsonElement("productId")]
    public string ProductId { get; set; } = string.Empty;

    [BsonElement("productName")]
    public string ProductName { get; set; } = string.Empty;

    [BsonElement("productSku")]
    public string? ProductSku { get; set; }

    /// <summary>
    /// Size giày EU (hợp lệ từ 30 đến 55 theo quy chuẩn Catalog).
    /// </summary>
    [BsonElement("size")]
    public int Size { get; set; }

    /// <summary>
    /// Số lượng đặt mua (tối thiểu là 1).
    /// </summary>
    [BsonElement("quantity")]
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// Đơn giá tham khảo tại thời điểm thêm vào giỏ (VNĐ).
    /// </summary>
    [BsonElement("unitPrice")]
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// URL ảnh đại diện của sản phẩm.
    /// </summary>
    [BsonElement("thumbnailUrl")]
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Tổng tiền dòng sản phẩm (UnitPrice * Quantity).
    /// </summary>
    [BsonIgnore]
    public decimal LineTotal => UnitPrice * Quantity;
}
