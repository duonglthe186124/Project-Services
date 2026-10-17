using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CatalogService.Models;

public class ShoeProduct
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    // Dòng giày thể thao: Running, Originals, Football, Basketball, Training, Lifestyle...
    [BsonElement("category")]
    public string Category { get; set; } = "Running";

    // Phân loại: Unisex (mặc định), Nam, Nữ
    [BsonElement("gender")]
    public string Gender { get; set; } = "Unisex";

    [BsonElement("description")]
    public string Description { get; set; } = string.Empty;

    // Giá gốc (VNĐ)
    [BsonElement("price")]
    public decimal Price { get; set; }

    // Giá khuyến mãi (VNĐ) - null nếu không sale
    [BsonElement("salePrice")]
    public decimal? SalePrice { get; set; }

    [BsonElement("color")]
    public string Color { get; set; } = string.Empty;

    // Bảng size chân kèm số lượng tồn kho của từng size
    [BsonElement("sizes")]
    public List<SizeStock> Sizes { get; set; } = new();

    // Tổng số lượng giày còn trong kho (tính tổng từ Sizes)
    [BsonElement("totalStock")]
    public int TotalStock => Sizes?.Sum(s => s.Quantity) ?? 0;

    // Link ảnh đại diện chính
    [BsonElement("thumbnailUrl")]
    public string ThumbnailUrl { get; set; } = string.Empty;

    // Album các góc ảnh (đế, gót, trên chân...)
    [BsonElement("imageUrls")]
    public List<string> ImageUrls { get; set; } = new();

    // Nhãn nổi bật: New Arrival, Best Seller, Hot Sale, Limited...
    [BsonElement("tags")]
    public List<string> Tags { get; set; } = new();

    // Điểm đánh giá trung bình (1.0 - 5.0)
    [BsonElement("averageRating")]
    public double AverageRating { get; set; } = 5.0;

    // Tổng số lượt đánh giá
    [BsonElement("reviewCount")]
    public int ReviewCount { get; set; } = 0;

    // Danh sách nhận xét & đánh giá sao của khách
    [BsonElement("reviews")]
    public List<ShoeReview> Reviews { get; set; } = new();

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}

public class SizeStock
{
    [BsonElement("size")]
    public int Size { get; set; } // Size giày EU (36, 37, 38, 39, 40, 41, 42, 43, 44, 45...)

    [BsonElement("quantity")]
    public int Quantity { get; set; } // Số lượng còn trong kho của size này
}

public class ShoeReview
{
    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("fullName")]
    public string FullName { get; set; } = string.Empty;

    [BsonElement("rating")]
    public int Rating { get; set; } // 1 - 5 sao

    [BsonElement("comment")]
    public string Comment { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
