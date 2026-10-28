using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OrderService.Data;
using OrderService.Dtos;
using OrderService.Models;

namespace OrderService.Services;

/// <inheritdoc />
public class OrderSagaOrchestrator : IOrderSagaOrchestrator
{
    private const string SystemPaymentActor = "System:PaymentCallback";

    private readonly IMongoCollection<OrderSagaState> _sagas;
    private readonly IMongoCollection<Order> _orders;
    private readonly IMongoCollection<OrderCompensation> _compensations;
    private readonly ICatalogServiceClient _catalog;
    private readonly IOrderEmailService? _email;
    private readonly ILogger<OrderSagaOrchestrator> _logger;

    public OrderSagaOrchestrator(IMongoDatabase database, ICatalogServiceClient catalog, ILogger<OrderSagaOrchestrator> logger,
        IOrderEmailService? email = null)
    {
        _sagas = database.GetCollection<OrderSagaState>(OrderDbInitializer.OrderSagaStatesCollectionName);
        _orders = database.GetCollection<Order>(OrderDbInitializer.OrdersCollectionName);
        _compensations = database.GetCollection<OrderCompensation>(OrderDbInitializer.OrderCompensationsCollectionName);
        _catalog = catalog;
        _email = email;
        _logger = logger;
    }

    // =====================================================================
    // CORE: OCC transition
    // =====================================================================

    /// <inheritdoc />
    public async Task<TransitionResult> TryTransitionAsync(
        string orderId, string expectedState, string newState, string trigger, string actor,
        string? notes = null, int? expectedVersion = null,
        UpdateDefinition<OrderSagaState>? extraUpdate = null, CancellationToken ct = default)
    {
        if (!SagaTransitions.IsAllowed(expectedState, newState))
        {
            return new TransitionResult(false, null, $"Transition {expectedState} -> {newState} không hợp lệ.");
        }

        var fb = Builders<OrderSagaState>.Filter;
        var filter = fb.Eq(s => s.OrderId, orderId) & fb.Eq(s => s.CurrentState, expectedState);
        if (expectedVersion.HasValue)
        {
            filter &= fb.Eq(s => s.Version, expectedVersion.Value);
        }

        var now = DateTime.UtcNow;
        var ub = Builders<OrderSagaState>.Update;
        var update = ub.Set(s => s.CurrentState, newState)
            .Set(s => s.UpdatedAt, now)
            .Inc(s => s.Version, 1)
            .Push(s => s.AuditTrail, new AuditLogEntry
            {
                FromState = expectedState,
                ToState = newState,
                TriggerAction = trigger,
                Actor = actor,
                Notes = notes,
                Timestamp = now
            });
        if (extraUpdate is not null)
        {
            update = ub.Combine(update, extraUpdate);
        }

        var saga = await _sagas.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<OrderSagaState> { ReturnDocument = ReturnDocument.After }, ct);

        if (saga is null)
        {
            _logger.LogWarning("OCC conflict: Order {OrderId} không còn ở trạng thái {Expected} (trigger {Trigger}).",
                orderId, expectedState, trigger);
            return new TransitionResult(false, null, $"Đơn hàng không còn ở trạng thái {expectedState} (đã bị thay đổi bởi tiến trình khác).");
        }

        // Đồng bộ Orders.orderStatus (Saga là nguồn sự thật; lệch tạm thời sẽ được reconcile ở M5)
        try
        {
            await _orders.UpdateOneAsync(o => o.Id == orderId,
                Builders<Order>.Update.Set(o => o.OrderStatus, newState).Set(o => o.UpdatedAt, now),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không đồng bộ được Orders.orderStatus cho {OrderId} -> {State}.", orderId, newState);
        }

        _logger.LogInformation("Saga {OrderId}: {From} -> {To} (v{Version}, {Trigger}, {Actor}).",
            orderId, expectedState, newState, saga.Version, trigger, actor);
        return new TransitionResult(true, saga, null);
    }

    // =====================================================================
    // CANCEL
    // =====================================================================

    /// <inheritdoc />
    public async Task<OrderOperationResult<CancelOrderResponse>> CancelAsync(
        string orderId, string userId, bool isAdmin, string? reason, CancellationToken ct = default)
    {
        var order = await _orders.Find(o => o.Id == orderId).FirstOrDefaultAsync(ct);
        if (order is null || !OrderAccessPolicy.CanCancel(order.UserId, userId, isAdmin))
        {
            return OrderOperationResult<CancelOrderResponse>.NotFound("Không tìm thấy đơn hàng.");
        }

        var saga = await _sagas.Find(s => s.OrderId == orderId).FirstOrDefaultAsync(ct);
        if (saga is null)
        {
            return OrderOperationResult<CancelOrderResponse>.NotFound("Không tìm thấy trạng thái Saga của đơn hàng.");
        }

        if (!OrderStatusConstants.CanBeCancelledByCustomer(saga.CurrentState))
        {
            return OrderOperationResult<CancelOrderResponse>.Conflict(
                $"Chỉ hủy được đơn ở trạng thái {OrderStatusConstants.AwaitingPayment}. Trạng thái hiện tại: {saga.CurrentState}.");
        }

        var isOwner = string.Equals(order.UserId, userId, StringComparison.Ordinal);
        var trigger = isOwner ? SagaTriggerConstants.CustomerCancelled : SagaTriggerConstants.AdminCancelled;
        var actor = isOwner ? $"Customer:{userId}" : $"Admin:{userId}";
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? "Không có lý do" : reason.Trim();

        var transition = await TryTransitionAsync(orderId, OrderStatusConstants.AwaitingPayment,
            OrderStatusConstants.Cancelled, trigger, actor, cleanReason, saga.Version, ct: ct);
        if (!transition.Success)
        {
            return OrderOperationResult<CancelOrderResponse>.Conflict(transition.ConflictReason ?? "Xung đột trạng thái.");
        }

        // Nhả kho; thất bại thì ghi outbox để OrderCompensationWorker (M5) retry
        var releaseRequest = new ReleaseStockRequest(orderId, $"{trigger}: {cleanReason}");
        string? releaseError = null;
        try
        {
            var result = await _catalog.ReleaseStockAsync(releaseRequest, ct);
            if (!result.Success && result.Status != "Released")
            {
                // 404 (chưa từng reserve) cũng coi như không cần nhả; các trường hợp khác đưa vào outbox
                if (!string.Equals(result.Status, "NotFound", StringComparison.OrdinalIgnoreCase))
                {
                    releaseError = $"{result.Status}: {result.Message}";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            releaseError = ex.Message;
        }

        if (releaseError is null)
        {
            await _sagas.UpdateOneAsync(s => s.OrderId == orderId,
                Builders<OrderSagaState>.Update.Set(s => s.StockReleasedAt, DateTime.UtcNow), cancellationToken: CancellationToken.None);
            return OrderOperationResult<CancelOrderResponse>.Ok(new CancelOrderResponse(
                orderId, OrderStatusConstants.Cancelled, true, false, "Đã hủy đơn và hoàn trả tồn kho."));
        }

        _logger.LogWarning("Release stock thất bại cho {OrderId}: {Error}. Ghi OrderCompensations.", orderId, releaseError);
        await EnqueueReleaseCompensationAsync(order, releaseRequest, releaseError);
        return OrderOperationResult<CancelOrderResponse>.Ok(new CancelOrderResponse(
            orderId, OrderStatusConstants.Cancelled, false, true,
            "Đã hủy đơn. Hoàn trả tồn kho đang được hệ thống xử lý lại tự động."));
    }

    private async Task EnqueueReleaseCompensationAsync(Order order, ReleaseStockRequest request, string error)
    {
        try
        {
            await _compensations.InsertOneAsync(new OrderCompensation
            {
                OrderId = order.Id ?? string.Empty,
                OrderCode = order.OrderCode,
                Action = "ReleaseStock",
                Payload = JsonSerializer.Serialize(request),
                Status = CompensationStatusConstants.Pending,
                LastError = error,
                NextRetryAt = DateTime.UtcNow.AddSeconds(20)
            });
        }
        catch (Exception ex)
        {
            // Catalog TTL worker (30s) vẫn là lưới an toàn cuối cùng khi hold hết hạn
            _logger.LogCritical(ex, "Không ghi được OrderCompensations cho {OrderId}.", order.Id);
        }
    }

    // =====================================================================
    // PAYMENT CALLBACK
    // =====================================================================

    /// <inheritdoc />
    public async Task<OrderOperationResult<PaymentCallbackResponse>> HandlePaymentCallbackAsync(
        PaymentCallbackRequest request, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.PaymentMethod) && !PaymentMethodConstants.IsValid(request.PaymentMethod))
        {
            return OrderOperationResult<PaymentCallbackResponse>.BadRequest(
                $"PaymentMethod không hợp lệ. Giá trị cho phép: {string.Join(", ", PaymentMethodConstants.All)}.");
        }

        // Thử tối đa 2 lần: lần 2 chạy khi thua OCC để đánh giá lại theo trạng thái mới (replay / late / conflict)
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var order = await _orders.Find(o => o.Id == request.OrderId).FirstOrDefaultAsync(ct);
            var saga = await _sagas.Find(s => s.OrderId == request.OrderId).FirstOrDefaultAsync(ct);
            if (order is null || saga is null)
            {
                return OrderOperationResult<PaymentCallbackResponse>.NotFound("Không tìm thấy đơn hàng.");
            }

            var isSuccess = string.Equals(request.Status, "Success", StringComparison.Ordinal);
            var decision = PaymentCallbackPolicy.Decide(saga.CurrentState, order.TotalAmount,
                order.PaymentTransactionId, request.PaymentTransactionId, request.Amount, isSuccess);

            switch (decision)
            {
                case PaymentCallbackDecision.IdempotentReplay:
                    return OrderOperationResult<PaymentCallbackResponse>.Ok(new PaymentCallbackResponse(
                        request.OrderId, saga.CurrentState, true,
                        saga.CurrentState == OrderStatusConstants.LatePaymentDetected,
                        "Callback đã được xử lý trước đó (idempotent replay)."));

                case PaymentCallbackDecision.AmountMismatch:
                    _logger.LogWarning("Callback {OrderId}: amount {Amount} khác TotalAmount {Total}.",
                        request.OrderId, request.Amount, order.TotalAmount);
                    return OrderOperationResult<PaymentCallbackResponse>.BadRequest(
                        $"Số tiền thanh toán ({request.Amount}) không khớp tổng đơn ({order.TotalAmount}).");

                case PaymentCallbackDecision.Conflict:
                    return OrderOperationResult<PaymentCallbackResponse>.Conflict(
                        $"Không thể xử lý callback ở trạng thái {saga.CurrentState}.");

                case PaymentCallbackDecision.RecordFailure:
                    return await RecordPaymentFailureAsync(request, saga, ct);

                case PaymentCallbackDecision.LatePayment:
                {
                    var late = await TryTransitionAsync(request.OrderId, saga.CurrentState,
                        OrderStatusConstants.LatePaymentDetected, SagaTriggerConstants.LatePaymentDetected,
                        SystemPaymentActor, $"Txn {request.PaymentTransactionId} đến sau khi đơn {saga.CurrentState}. Cần hoàn tiền.",
                        saga.Version,
                        Builders<OrderSagaState>.Update.Set(s => s.FailureReason, "LatePayment_RefundRequired"), ct);
                    if (!late.Success) continue;

                    await SetPaymentInfoAsync(request, ct);
                    return OrderOperationResult<PaymentCallbackResponse>.Ok(new PaymentCallbackResponse(
                        request.OrderId, OrderStatusConstants.LatePaymentDetected, false, true,
                        "Thanh toán đến muộn: đơn đã hết hạn/hủy. KHÔNG xuất kho, cần hoàn tiền cho khách."));
                }

                case PaymentCallbackDecision.Verify:
                {
                    var verified = await TryTransitionAsync(request.OrderId, OrderStatusConstants.AwaitingPayment,
                        OrderStatusConstants.PaymentVerified, SagaTriggerConstants.PaymentCallbackSuccess,
                        SystemPaymentActor, $"Txn {request.PaymentTransactionId}, amount {request.Amount}", saga.Version, ct: ct);
                    if (!verified.Success) continue;

                    await SetPaymentInfoAsync(request, ct);
                    return await ConfirmStockAfterPaymentAsync(request.OrderId, ct);
                }
            }
        }

        return OrderOperationResult<PaymentCallbackResponse>.Conflict("Trạng thái đơn thay đổi liên tục, vui lòng gửi lại callback.");
    }

    private async Task<OrderOperationResult<PaymentCallbackResponse>> ConfirmStockAfterPaymentAsync(string orderId, CancellationToken ct)
    {
        StockOperationResult confirm;
        try
        {
            confirm = await _catalog.ConfirmStockAsync(new ConfirmStockRequest(orderId, "PaymentVerified"), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException
                                       or Polly.CircuitBreaker.BrokenCircuitException or Polly.Timeout.TimeoutRejectedException)
        {
            // Catalog không phản hồi: giữ PaymentVerified, worker M5 sẽ confirm lại
            _logger.LogError(ex, "Confirm stock lỗi mạng cho {OrderId}. Giữ PaymentVerified.", orderId);
            await _sagas.UpdateOneAsync(s => s.OrderId == orderId,
                Builders<OrderSagaState>.Update.Set(s => s.FailureReason, $"ConfirmPending: {ex.Message}"),
                cancellationToken: CancellationToken.None);
            return OrderOperationResult<PaymentCallbackResponse>.Accepted(new PaymentCallbackResponse(
                orderId, OrderStatusConstants.PaymentVerified, false, false,
                "Đã ghi nhận thanh toán. Xác nhận kho đang chờ xử lý lại."), "ConfirmPending");
        }

        if (confirm.Success)
        {
            var ready = await TryTransitionAsync(orderId, OrderStatusConstants.PaymentVerified,
                OrderStatusConstants.ReadyForDispatch, SagaTriggerConstants.StockConfirmed, SystemPaymentActor,
                "Catalog đã xác nhận xuất kho.",
                extraUpdate: Builders<OrderSagaState>.Update
                    .Set(s => s.StockConfirmedAt, DateTime.UtcNow)
                    .Set(s => s.FailureReason, null), ct: ct);
            var state = ready.Success ? OrderStatusConstants.ReadyForDispatch : OrderStatusConstants.PaymentVerified;
            if (ready.Success)
            {
                await FinalizeDispatchAsync(orderId, ct);
            }
            return OrderOperationResult<PaymentCallbackResponse>.Ok(new PaymentCallbackResponse(
                orderId, state, false, false, "Thanh toán thành công, đơn sẵn sàng đóng gói."));
        }

        // Catalog từ chối (409 Released/Expired, 404): tiền đã nhận nhưng kho đã nhả → late payment, cần hoàn tiền
        await TryTransitionAsync(orderId, OrderStatusConstants.PaymentVerified,
            OrderStatusConstants.LatePaymentDetected, SagaTriggerConstants.StockConfirmRejected, SystemPaymentActor,
            $"Catalog từ chối confirm: {confirm.Status} - {confirm.Message}",
            extraUpdate: Builders<OrderSagaState>.Update.Set(s => s.FailureReason, $"StockConfirmRejected: {confirm.Status}"), ct: ct);
        return OrderOperationResult<PaymentCallbackResponse>.Ok(new PaymentCallbackResponse(
            orderId, OrderStatusConstants.LatePaymentDetected, false, true,
            "Kho đã được nhả trước khi thanh toán về. KHÔNG xuất kho, cần hoàn tiền cho khách."));
    }

    private async Task<OrderOperationResult<PaymentCallbackResponse>> RecordPaymentFailureAsync(
        PaymentCallbackRequest request, OrderSagaState saga, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var fb = Builders<OrderSagaState>.Filter;
        await _sagas.UpdateOneAsync(
            fb.Eq(s => s.OrderId, request.OrderId) & fb.Eq(s => s.CurrentState, OrderStatusConstants.AwaitingPayment),
            Builders<OrderSagaState>.Update
                .Inc(s => s.Version, 1)
                .Set(s => s.UpdatedAt, now)
                .Push(s => s.AuditTrail, new AuditLogEntry
                {
                    FromState = saga.CurrentState,
                    ToState = saga.CurrentState,
                    TriggerAction = SagaTriggerConstants.PaymentCallbackFailed,
                    Actor = SystemPaymentActor,
                    Notes = $"Txn {request.PaymentTransactionId} thất bại. Khách có thể thử lại trước {saga.ExpiresAt:O}.",
                    Timestamp = now
                }), cancellationToken: ct);

        return OrderOperationResult<PaymentCallbackResponse>.Ok(new PaymentCallbackResponse(
            request.OrderId, OrderStatusConstants.AwaitingPayment, false, false,
            "Đã ghi nhận thanh toán thất bại. Đơn vẫn chờ thanh toán trong thời hạn giữ hàng."));
    }

    private Task SetPaymentInfoAsync(PaymentCallbackRequest request, CancellationToken ct)
    {
        var update = Builders<Order>.Update
            .Set(o => o.PaymentTransactionId, request.PaymentTransactionId)
            .Set(o => o.PaidAt, request.CallbackTimestamp?.ToUniversalTime() ?? DateTime.UtcNow)
            .Set(o => o.UpdatedAt, DateTime.UtcNow);
        if (PaymentMethodConstants.IsValid(request.PaymentMethod))
        {
            update = update.Set(o => o.PaymentMethod, PaymentMethodConstants.Normalize(request.PaymentMethod!));
        }
        return _orders.UpdateOneAsync(o => o.Id == request.OrderId, update, cancellationToken: ct);
    }

    /// <summary>
    /// Chạy đúng một lần khi đơn chuyển sang ReadyForDispatch: sinh mã vận đơn (chỉ ghi nếu chưa có) rồi gửi email xác nhận.
    /// Mọi lỗi đều được nuốt + log: callback thanh toán không bao giờ thất bại vì email/mã vận đơn.
    /// </summary>
    private async Task FinalizeDispatchAsync(string orderId, CancellationToken ct)
    {
        try
        {
            var fb = Builders<Order>.Filter;
            await _orders.UpdateOneAsync(
                fb.Eq(o => o.Id, orderId) & fb.Eq(o => o.WaybillCode, null),
                Builders<Order>.Update.Set(o => o.WaybillCode, WaybillCodeGenerator.Generate()).Set(o => o.UpdatedAt, DateTime.UtcNow),
                cancellationToken: ct);

            if (_email is null) return;
            var order = await _orders.Find(o => o.Id == orderId).FirstOrDefaultAsync(ct);
            if (order is not null)
            {
                await _email.SendOrderConfirmationAsync(order, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FinalizeDispatch (mã vận đơn/email) lỗi cho {OrderId}; bỏ qua, đơn vẫn ReadyForDispatch.", orderId);
        }
    }
}
