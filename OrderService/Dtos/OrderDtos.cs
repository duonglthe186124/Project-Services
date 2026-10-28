using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using OrderService.Models;

namespace OrderService.Dtos;

/// <summary>Dòng tóm tắt đơn hàng cho danh sách (my-orders / admin list).</summary>
public record OrderSummaryDto(
    string Id,
    string OrderCode,
    string UserId,
    string OrderStatus,
    string PaymentMethod,
    decimal TotalAmount,
    int TotalQuantity,
    bool IsHighValue,
    bool IsVipBulk,
    DateTime CreatedAt)
{
    public static OrderSummaryDto FromDomain(Order o) => new(
        o.Id ?? string.Empty, o.OrderCode, o.UserId, o.OrderStatus, o.PaymentMethod,
        o.TotalAmount, o.TotalQuantity, o.IsHighValue, o.IsVipBulk, o.CreatedAt);
}

/// <summary>Danh sách phân trang.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

/// <summary>Chi tiết đơn hàng. Trạng thái lấy từ OrderSagaState (nguồn sự thật) nếu có.</summary>
public record OrderDetailDto(
    string Id,
    string OrderCode,
    string UserId,
    string CustomerName,
    string CustomerPhone,
    ShippingAddress ShippingAddress,
    List<OrderItem> Items,
    decimal SubTotal,
    decimal ShippingFee,
    decimal DiscountAmount,
    decimal TotalAmount,
    string PaymentMethod,
    string OrderStatus,
    int SagaVersion,
    DateTime? ExpiresAt,
    bool IsHighValue,
    bool IsVipBulk,
    bool RequiresManualApproval,
    bool IsManuallyApproved,
    string? DeliveryOtpCode,
    string? SecuritySealCode,
    string? PaymentTransactionId,
    DateTime? PaidAt,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? CustomerEmail = null,
    string? WaybillCode = null)
{
    /// <param name="isOwner">Chỉ chủ đơn thấy DeliveryOtpCode.</param>
    /// <param name="isStaff">Chỉ Admin/LogisticsStaff thấy SecuritySealCode.</param>
    public static OrderDetailDto FromDomain(Order o, OrderSagaState? saga, bool isOwner, bool isStaff) => new(
        o.Id ?? string.Empty, o.OrderCode, o.UserId, o.CustomerName, o.CustomerPhone,
        o.ShippingAddress, o.Items, o.SubTotal, o.ShippingFee, o.DiscountAmount, o.TotalAmount,
        o.PaymentMethod, saga?.CurrentState ?? o.OrderStatus, saga?.Version ?? 0, saga?.ExpiresAt,
        o.IsHighValue, o.IsVipBulk, o.RequiresManualApproval, o.IsManuallyApproved,
        isOwner ? o.DeliveryOtpCode : null,
        isStaff ? o.SecuritySealCode : null,
        o.PaymentTransactionId, o.PaidAt, saga?.FailureReason, o.CreatedAt, o.UpdatedAt,
        o.CustomerEmail, o.WaybillCode);
}

/// <summary>Một mốc trong timeline (AuditTrail).</summary>
public record TimelineEntryDto(string FromState, string ToState, string TriggerAction, DateTime Timestamp, string Actor, string? Notes);

public record OrderTimelineDto(string OrderId, string OrderCode, string CurrentState, int Version, List<TimelineEntryDto> Entries)
{
    public static OrderTimelineDto FromDomain(OrderSagaState s) => new(
        s.OrderId, s.OrderCode, s.CurrentState, s.Version,
        s.AuditTrail.OrderBy(a => a.Timestamp)
            .Select(a => new TimelineEntryDto(a.FromState, a.ToState, a.TriggerAction, a.Timestamp, a.Actor, a.Notes))
            .ToList());
}

/// <summary>Body hủy đơn.</summary>
public record CancelOrderRequest(
    [property: MaxLength(500, ErrorMessage = "Lý do hủy tối đa 500 ký tự.")]
    string? Reason)
{
    public CancelOrderRequest() : this((string?)null) { }
}

/// <summary>
/// Payload callback thanh toán nội bộ (PaymentService → OrderService).
/// Khớp với OrderService.http: orderId, paymentTransactionId, amount, paymentMethod, callbackTimestamp.
/// Status: "Success" (mặc định) hoặc "Failed".
/// </summary>
public record PaymentCallbackRequest(
    [property: Required(ErrorMessage = "OrderId là bắt buộc.")]
    [property: MongoObjectId]
    string OrderId,

    [property: Required(ErrorMessage = "PaymentTransactionId là bắt buộc.")]
    [property: MaxLength(100, ErrorMessage = "PaymentTransactionId tối đa 100 ký tự.")]
    string PaymentTransactionId,

    [property: Range(typeof(decimal), "0.01", "100000000000", ErrorMessage = "Amount phải lớn hơn 0.",
        ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    decimal Amount,

    [property: MaxLength(30)]
    string? PaymentMethod = null,

    DateTime? CallbackTimestamp = null,

    [property: RegularExpression("^(Success|Failed)$", ErrorMessage = "Status chỉ nhận 'Success' hoặc 'Failed'.")]
    string Status = "Success")
{
    public PaymentCallbackRequest() : this(string.Empty, string.Empty, 0m) { }
}

/// <summary>Kết quả xử lý callback.</summary>
public record PaymentCallbackResponse(
    string OrderId,
    string Status,
    bool IsReplay,
    bool RefundRequired,
    string Message);

/// <summary>Kết quả hủy đơn.</summary>
public record CancelOrderResponse(string OrderId, string Status, bool StockReleased, bool CompensationQueued, string Message);
