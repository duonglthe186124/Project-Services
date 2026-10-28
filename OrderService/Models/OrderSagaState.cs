using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Lưu trữ trạng thái điều phối Saga In-Process của Đơn hàng (Collection "OrderSagaStates").
/// Mọi thay đổi trạng thái phải thông qua atomic FindOneAndUpdateAsync kiểm tra (OrderId, Version, CurrentState).
/// </summary>
public class OrderSagaState
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// Khóa ngoại liên kết với Orders.Id (Unique Index ux_orderId).
    /// </summary>
    [BsonElement("orderId")]
    public string OrderId { get; set; } = string.Empty;

    [BsonElement("orderCode")]
    public string OrderCode { get; set; } = string.Empty;

    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Trạng thái hiện tại của Saga (Compound Index ix_currentState_expiresAt).
    /// </summary>
    [BsonElement("currentState")]
    public string CurrentState { get; set; } = OrderStatusConstants.AwaitingPayment;

    /// <summary>
    /// Bộ đếm Version dùng cho Optimistic Concurrency Control (OCC).
    /// Tăng dần 1 đơn vị sau mỗi lần chuyển trạng thái thành công.
    /// </summary>
    [BsonElement("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Thời gian tạm giữ tồn kho (phút), mặc định 15 phút.
    /// </summary>
    [BsonElement("holdMinutes")]
    public int HoldMinutes { get; set; } = 15;

    /// <summary>
    /// Thời điểm hết hạn thanh toán (UTC) (Compound Index ix_currentState_expiresAt).
    /// Quá thời điểm này, OrderSagaTimeoutWorker sẽ thu hồi đơn và nhả tồn kho.
    /// </summary>
    [BsonElement("expiresAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ExpiresAt { get; set; }

    [BsonElement("stockReservedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? StockReservedAt { get; set; }

    [BsonElement("stockConfirmedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? StockConfirmedAt { get; set; }

    [BsonElement("stockReleasedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? StockReleasedAt { get; set; }

    [BsonElement("failureReason")]
    public string? FailureReason { get; set; }

    /// <summary>
    /// Sổ cái vết kiểm toán bất biến (Immutable Append-Only Audit Trail) ghi lại mọi lần chuyển trạng thái.
    /// </summary>
    [BsonElement("auditTrail")]
    public List<AuditLogEntry> AuditTrail { get; set; } = new();

    [BsonElement("createdAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? UpdatedAt { get; set; }
}
