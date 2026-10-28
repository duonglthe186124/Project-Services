using System;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Bản ghi kiểm toán bất biến (Append-Only) mô tả một bước chuyển trạng thái trong Saga Workflow.
/// </summary>
public class AuditLogEntry
{
    [BsonElement("fromState")]
    public string FromState { get; set; } = string.Empty;

    [BsonElement("toState")]
    public string ToState { get; set; } = string.Empty;

    [BsonElement("triggerAction")]
    public string TriggerAction { get; set; } = string.Empty;

    [BsonElement("timestamp")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tác nhân kích hoạt bước chuyển (ví dụ: UserId, "System:OrderSagaTimeoutWorker", "Admin:hoangnd").
    /// </summary>
    [BsonElement("actor")]
    public string Actor { get; set; } = string.Empty;

    [BsonElement("notes")]
    public string? Notes { get; set; }
}
