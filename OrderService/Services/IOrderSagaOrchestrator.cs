using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using OrderService.Dtos;
using OrderService.Models;

namespace OrderService.Services;

/// <summary>Loại kết quả nghiệp vụ, controller map sang HTTP status.</summary>
public enum OrderResultKind { Ok, Accepted, BadRequest, NotFound, Conflict, ServiceUnavailable }

public record OrderOperationResult<T>(OrderResultKind Kind, T? Value, string Message)
{
    public static OrderOperationResult<T> Ok(T value, string message = "") => new(OrderResultKind.Ok, value, message);
    public static OrderOperationResult<T> Accepted(T value, string message) => new(OrderResultKind.Accepted, value, message);
    public static OrderOperationResult<T> BadRequest(string message) => new(OrderResultKind.BadRequest, default, message);
    public static OrderOperationResult<T> NotFound(string message) => new(OrderResultKind.NotFound, default, message);
    public static OrderOperationResult<T> Conflict(string message) => new(OrderResultKind.Conflict, default, message);
    public static OrderOperationResult<T> ServiceUnavailable(string message) => new(OrderResultKind.ServiceUnavailable, default, message);
}

/// <summary>Kết quả một lần chuyển trạng thái OCC.</summary>
public record TransitionResult(bool Success, OrderSagaState? Saga, string? ConflictReason);

/// <summary>
/// Bộ điều phối Saga In-Process của OrderService.
/// Mọi thay đổi trạng thái đơn đi qua <see cref="TryTransitionAsync"/> (atomic FindOneAndUpdate + Version OCC).
/// </summary>
public interface IOrderSagaOrchestrator
{
    /// <summary>
    /// Chuyển trạng thái nguyên tử: filter (orderId, currentState = expectedState [, version = expectedVersion]),
    /// $set currentState, $inc version, $push auditTrail. Thành công thì đồng bộ Orders.orderStatus.
    /// </summary>
    Task<TransitionResult> TryTransitionAsync(
        string orderId, string expectedState, string newState, string trigger, string actor,
        string? notes = null, int? expectedVersion = null,
        UpdateDefinition<OrderSagaState>? extraUpdate = null, CancellationToken ct = default);

    /// <summary>Hủy đơn (chỉ khi AwaitingPayment) + nhả kho; lỗi nhả kho thì ghi outbox OrderCompensations.</summary>
    Task<OrderOperationResult<CancelOrderResponse>> CancelAsync(
        string orderId, string userId, bool isAdmin, string? reason, CancellationToken ct = default);

    /// <summary>Xử lý callback thanh toán: idempotent, chống late payment, confirm kho.</summary>
    Task<OrderOperationResult<PaymentCallbackResponse>> HandlePaymentCallbackAsync(
        PaymentCallbackRequest request, CancellationToken ct = default);
}
