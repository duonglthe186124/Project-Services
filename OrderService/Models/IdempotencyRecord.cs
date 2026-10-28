using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Bản ghi đảm bảo tính lũy đẳng (Idempotency) cho các thao tác đặt hàng nhạy cảm (Collection "IdempotencyRecords").
/// Tự động hết hạn và bị xóa khỏi MongoDB sau 24h nhờ TTL Index (ttl_expiresAt).
/// </summary>
public class IdempotencyRecord
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// Khóa lũy đẳng truyền từ client qua header X-Idempotency-Key (Unique Index ux_key).
    /// </summary>
    [BsonElement("key")]
    public string Key { get; set; } = string.Empty;

    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Mã băm SHA-256 của nội dung request để phát hiện trường hợp dùng cùng key nhưng đổi body.
    /// </summary>
    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>
    /// HTTP Status Code của lần thực thi đầu tiên (ví dụ 201 Created).
    /// </summary>
    [BsonElement("responseStatusCode")]
    public int ResponseStatusCode { get; set; }

    /// <summary>
    /// Nội dung JSON của phản hồi đã trả về lần đầu tiên.
    /// </summary>
    [BsonElement("responseBody")]
    public string ResponseBody { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Mốc thời gian hết hạn của bản ghi (TTL Index ttl_expiresAt với ExpireAfter = TimeSpan.Zero).
    /// Mặc định: CreatedAt + 24 giờ.
    /// </summary>
    [BsonElement("expiresAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);

    /// <summary>
    /// Bí danh hỗ trợ cho thuộc tính IdempotencyKey.
    /// </summary>
    [BsonIgnore]
    public string IdempotencyKey
    {
        get => Key;
        set => Key = value;
    }
}
