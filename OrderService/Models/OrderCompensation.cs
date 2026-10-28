using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Bản ghi Transactional Outbox phục vụ cơ chế tự chữa lành (Self-Healing) cho các bước bù trừ Saga
/// (ví dụ: lệnh nhả kho ReleaseStock khi CatalogService gặp sự cố mạng hoặc timeout).
/// Được quét định kỳ mỗi 20s bởi OrderCompensationWorker.
/// </summary>
public class OrderCompensation
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("orderId")]
    public string OrderId { get; set; } = string.Empty;

    [BsonElement("orderCode")]
    public string OrderCode { get; set; } = string.Empty;

    /// <summary>
    /// Tên hành động bù trừ (mặc định: "ReleaseStock").
    /// </summary>
    [BsonElement("action")]
    public string Action { get; set; } = "ReleaseStock";

    /// <summary>
    /// Chuỗi JSON tuần tự hóa chứa thông tin request cần gửi sang đối tác (ví dụ ReleaseStockRequest).
    /// </summary>
    [BsonElement("payload")]
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Trạng thái outbox: Pending, Processing, Succeeded, Failed (Compound Index ix_status_nextRetryAt).
    /// </summary>
    [BsonElement("status")]
    public string Status { get; set; } = CompensationStatusConstants.Pending;

    [BsonElement("retryCount")]
    public int RetryCount { get; set; } = 0;

    [BsonElement("maxRetries")]
    public int MaxRetries { get; set; } = 10;

    /// <summary>
    /// Thời điểm được phép retry lần tiếp theo (UTC) theo công thức Exponential Backoff.
    /// </summary>
    [BsonElement("nextRetryAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime NextRetryAt { get; set; } = DateTime.UtcNow;

    [BsonElement("lastError")]
    public string? LastError { get; set; }

    [BsonElement("createdAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? UpdatedAt { get; set; }
}
