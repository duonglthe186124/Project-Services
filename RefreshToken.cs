using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AuthService.Models;

/// <summary>
/// Refresh Token lưu trong MongoDB (collection "RefreshTokens").
/// Chỉ lưu HASH (SHA-256) của token, không lưu token gốc -> lộ DB cũng không dùng được token.
/// </summary>
public class RefreshToken
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("tokenHash")]
    public string TokenHash { get; set; } = string.Empty;

    [BsonElement("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("createdByIp")]
    public string? CreatedByIp { get; set; }

    [BsonElement("userAgent")]
    public string? UserAgent { get; set; }

    // Thời điểm bị thu hồi (do xoay vòng, đăng xuất, đổi mật khẩu...). null = còn hiệu lực
    [BsonElement("revokedAt")]
    public DateTime? RevokedAt { get; set; }

    [BsonElement("revokedReason")]
    public string? RevokedReason { get; set; }
}
