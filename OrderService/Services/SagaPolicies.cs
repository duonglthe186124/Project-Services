using System;
using System.Collections.Generic;
using OrderService.Models;

namespace OrderService.Services;

/// <summary>
/// Ma trận chuyển trạng thái Saga hợp lệ (hard-code). Mọi transition ngoài bảng bị từ chối trước khi chạm DB.
/// </summary>
public static class SagaTransitions
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        [OrderStatusConstants.AwaitingPayment] = new()
        {
            OrderStatusConstants.PaymentVerified,
            OrderStatusConstants.Cancelled,
            OrderStatusConstants.Expired
        },
        [OrderStatusConstants.PaymentVerified] = new()
        {
            OrderStatusConstants.ReadyForDispatch,
            OrderStatusConstants.LatePaymentDetected
        },
        [OrderStatusConstants.ReadyForDispatch] = new() { OrderStatusConstants.WarehousePacking },
        [OrderStatusConstants.WarehousePacking] = new() { OrderStatusConstants.CarrierAssigned },
        [OrderStatusConstants.CarrierAssigned] = new() { OrderStatusConstants.InTransit },
        [OrderStatusConstants.InTransit] = new() { OrderStatusConstants.Delivered },
        [OrderStatusConstants.Delivered] = new() { OrderStatusConstants.Completed },
        [OrderStatusConstants.Expired] = new() { OrderStatusConstants.LatePaymentDetected },
        [OrderStatusConstants.Cancelled] = new() { OrderStatusConstants.LatePaymentDetected },
    };

    public static bool IsAllowed(string? from, string? to) =>
        from is not null && to is not null && Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
}

/// <summary>Kết quả quyết định xử lý payment callback dựa trên trạng thái hiện tại.</summary>
public enum PaymentCallbackDecision
{
    /// <summary>Chuyển AwaitingPayment → PaymentVerified rồi confirm kho.</summary>
    Verify,
    /// <summary>Callback trùng (cùng transactionId) cho đơn đã xử lý → trả 200 replay.</summary>
    IdempotentReplay,
    /// <summary>Đơn đã Expired/Cancelled → LatePaymentDetected, KHÔNG confirm kho.</summary>
    LatePayment,
    /// <summary>Số tiền không khớp TotalAmount → 400.</summary>
    AmountMismatch,
    /// <summary>Thanh toán thất bại → chỉ ghi audit, giữ AwaitingPayment.</summary>
    RecordFailure,
    /// <summary>Trạng thái không cho phép (VD: đã thanh toán bằng transaction khác) → 409.</summary>
    Conflict
}

public static class PaymentCallbackPolicy
{
    public static PaymentCallbackDecision Decide(
        string currentState, decimal orderTotal, string? existingTransactionId,
        string transactionId, decimal amount, bool isSuccess)
    {
        var sameTxn = !string.IsNullOrEmpty(existingTransactionId) &&
                      string.Equals(existingTransactionId, transactionId, StringComparison.Ordinal);

        // Replay trước để callback lặp lại luôn an toàn
        if (sameTxn && currentState is not OrderStatusConstants.AwaitingPayment)
            return PaymentCallbackDecision.IdempotentReplay;

        if (!isSuccess)
            return currentState == OrderStatusConstants.AwaitingPayment
                ? PaymentCallbackDecision.RecordFailure
                : PaymentCallbackDecision.Conflict;

        if (amount != orderTotal)
            return PaymentCallbackDecision.AmountMismatch;

        return currentState switch
        {
            OrderStatusConstants.AwaitingPayment => PaymentCallbackDecision.Verify,
            OrderStatusConstants.Expired or OrderStatusConstants.Cancelled => PaymentCallbackDecision.LatePayment,
            _ => PaymentCallbackDecision.Conflict
        };
    }
}

/// <summary>Quy tắc truy cập đơn hàng (tenant isolation).</summary>
public static class OrderAccessPolicy
{
    public static bool IsStaff(bool isAdmin, bool isLogistics) => isAdmin || isLogistics;

    /// <summary>Chủ đơn hoặc Admin/LogisticsStaff mới được xem. Người khác nhận 404.</summary>
    public static bool CanView(string? ownerId, string? userId, bool isAdmin, bool isLogistics) =>
        IsStaff(isAdmin, isLogistics) ||
        (!string.IsNullOrEmpty(userId) && string.Equals(ownerId, userId, StringComparison.Ordinal));

    /// <summary>Chỉ chủ đơn hoặc Admin được hủy (LogisticsStaff không hủy đơn).</summary>
    public static bool CanCancel(string? ownerId, string? userId, bool isAdmin) =>
        isAdmin || (!string.IsNullOrEmpty(userId) && string.Equals(ownerId, userId, StringComparison.Ordinal));
}
