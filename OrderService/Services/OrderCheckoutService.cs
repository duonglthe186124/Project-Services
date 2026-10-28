using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OrderService.Data;
using OrderService.Dtos;
using OrderService.Models;

namespace OrderService.Services;

/// <inheritdoc />
public class OrderCheckoutService : IOrderCheckoutService
{
    private const int HoldMinutes = OrderTierConstants.DefaultHoldMinutes;

    private readonly IMongoCollection<Order> _orders;
    private readonly IMongoCollection<OrderSagaState> _sagas;
    private readonly ICatalogServiceClient _catalog;
    private readonly ICartService _cart;
    private readonly ILogger<OrderCheckoutService> _logger;

    public OrderCheckoutService(IMongoDatabase database, ICatalogServiceClient catalog, ICartService cart, ILogger<OrderCheckoutService> logger)
    {
        _orders = database.GetCollection<Order>(OrderDbInitializer.OrdersCollectionName);
        _sagas = database.GetCollection<OrderSagaState>(OrderDbInitializer.OrderSagaStatesCollectionName);
        _catalog = catalog;
        _cart = cart;
        _logger = logger;
    }

    public async Task<OrderOperationResult<CheckoutResponse>> CheckoutAsync(
        string userId, string? tokenEmail, string? tokenFullName, CheckoutRequest request, CancellationToken ct = default)
    {
        // ---- 1. Thông tin người nhận ----
        var customerName = FirstNonBlank(request.CustomerName, tokenFullName);
        if (customerName is null)
        {
            return OrderOperationResult<CheckoutResponse>.BadRequest("Thiếu tên người nhận (CustomerName) và token không có claim fullName.");
        }

        var customerEmail = ResolveEmail(request.CustomerEmail, tokenEmail);
        if (customerEmail is null)
        {
            return OrderOperationResult<CheckoutResponse>.BadRequest("Thiếu email nhận xác nhận đơn: hãy truyền CustomerEmail hoặc đăng nhập bằng tài khoản có email.");
        }

        // ---- 2. Nguồn hàng: body.items ưu tiên, nếu không thì giỏ hàng ----
        var fromCart = request.Items is null || request.Items.Count == 0;
        IEnumerable<(string ProductId, int Size, int Quantity)> source;
        if (fromCart)
        {
            var cart = await _cart.GetOrCreateCartAsync(userId, ct);
            source = cart.Items.Select(i => (i.ProductId, i.Size, i.Quantity));
        }
        else
        {
            source = request.Items!.Select(i => (i.ProductId, i.Size, i.Quantity));
        }

        var (lines, mergeError) = MergeLines(source);
        if (mergeError is not null)
        {
            return OrderOperationResult<CheckoutResponse>.BadRequest(mergeError);
        }
        if (lines.Count == 0)
        {
            return OrderOperationResult<CheckoutResponse>.BadRequest("Giỏ hàng trống: hãy thêm sản phẩm hoặc truyền danh sách items.");
        }

        // ---- 3. Sinh OrderId/OrderCode (kiểm tra trùng trước khi khóa kho) ----
        var orderId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var orderCode = await GenerateUniqueOrderCodeAsync(ct);

        // ---- 4. Khóa kho nguyên tử tại Catalog (TTL 15 phút) ----
        StockOperationResult reserve;
        try
        {
            reserve = await _catalog.ReserveStockAsync(new ReserveStockRequest(orderId, orderCode, userId, lines, HoldMinutes), ct);
        }
        catch (Exception ex) when (IsCatalogUnavailable(ex))
        {
            _logger.LogError(ex, "Catalog không phản hồi khi reserve cho order {OrderId}.", orderId);
            return OrderOperationResult<CheckoutResponse>.ServiceUnavailable("Dịch vụ kho hàng tạm thời không phản hồi. Vui lòng thử lại sau.");
        }

        if (!reserve.Success)
        {
            var message = DescribeStockFailure(reserve);
            return string.Equals(reserve.Status, "InvalidProductId", StringComparison.OrdinalIgnoreCase)
                ? OrderOperationResult<CheckoutResponse>.BadRequest(message)
                : OrderOperationResult<CheckoutResponse>.Conflict(message);
        }

        var reservation = reserve.Reservation;
        if (reservation is null)
        {
            try { reservation = await _catalog.GetReservationAsync(orderId, ct); }
            catch (Exception ex) when (IsCatalogUnavailable(ex)) { /* xử lý bên dưới */ }
        }
        if (reservation is null || reservation.Items.Count == 0)
        {
            await TryReleaseAsync(orderId, "CheckoutNoReservationSnapshot");
            return OrderOperationResult<CheckoutResponse>.ServiceUnavailable("Không lấy được snapshot giá từ kho. Vui lòng thử lại.");
        }

        // ---- 5. Dựng Order từ snapshot Catalog (Zero-Trust: không tin giá phía client) ----
        var now = DateTime.UtcNow;
        var items = reservation.Items.Select(s => new OrderItem
        {
            ProductId = s.ProductId,
            ProductName = s.ProductName,
            Category = s.Category,
            Color = s.Color,
            Size = s.Size,
            Quantity = s.Quantity,
            UnitPrice = s.UnitPriceSnapshot,
            LineTotal = s.LineTotal,
            ThumbnailUrl = s.ThumbnailUrl ?? string.Empty
        }).ToList();

        var subTotal = items.Sum(i => i.LineTotal);
        var address = request.ShippingAddress;
        var order = new Order
        {
            Id = orderId,
            OrderCode = orderCode,
            UserId = userId,
            CustomerName = customerName,
            CustomerPhone = request.CustomerPhone.Trim(),
            CustomerEmail = customerEmail,
            ShippingAddress = new ShippingAddress
            {
                AddressLine = address.AddressLine.Trim(),
                Ward = address.Ward.Trim(),
                District = address.District.Trim(),
                City = address.City.Trim(),
                Latitude = address.Latitude,
                Longitude = address.Longitude
            },
            Items = items,
            SubTotal = subTotal,
            ShippingFee = 0m,
            DiscountAmount = 0m,
            TotalAmount = subTotal,
            PaymentMethod = string.Empty, // Khách chọn COD/Online_QR ở bước PaymentService
            OrderStatus = OrderStatusConstants.AwaitingPayment,
            ReservationId = reservation.Id,
            CreatedAt = now
        };

        var expiresAt = reservation.ExpiresAt == default ? now.AddMinutes(HoldMinutes) : reservation.ExpiresAt.ToUniversalTime();
        var saga = new OrderSagaState
        {
            OrderId = orderId,
            OrderCode = orderCode,
            UserId = userId,
            CurrentState = OrderStatusConstants.AwaitingPayment,
            Version = 1,
            HoldMinutes = HoldMinutes,
            ExpiresAt = expiresAt,
            StockReservedAt = now,
            CreatedAt = now,
            AuditTrail =
            {
                new AuditLogEntry
                {
                    FromState = string.Empty,
                    ToState = OrderStatusConstants.AwaitingPayment,
                    TriggerAction = SagaTriggerConstants.CheckoutSuccess,
                    Actor = $"Customer:{userId}",
                    Notes = $"Checkout {(fromCart ? "từ giỏ hàng" : "mua ngay")}, {order.TotalQuantity} sản phẩm, tổng {order.TotalAmount:0}",
                    Timestamp = now
                }
            }
        };

        // ---- 6. Lưu Order + Saga; lỗi → nhả kho (Catalog TTL là lưới an toàn cuối) ----
        try
        {
            await _orders.InsertOneAsync(order, cancellationToken: ct);
            try
            {
                await _sagas.InsertOneAsync(saga, cancellationToken: ct);
            }
            catch
            {
                await _orders.DeleteOneAsync(o => o.Id == orderId, CancellationToken.None);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không lưu được đơn {OrderId}. Nhả kho.", orderId);
            await TryReleaseAsync(orderId, "CheckoutPersistFailed");
            return OrderOperationResult<CheckoutResponse>.ServiceUnavailable("Không thể tạo đơn hàng lúc này. Vui lòng thử lại.");
        }

        // ---- 7. Dọn giỏ hàng (chỉ khi mua từ giỏ) — lỗi không làm hỏng đơn ----
        if (fromCart)
        {
            try { await _cart.ClearCartAsync(userId, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Không dọn được giỏ hàng của {UserId} sau khi tạo đơn {OrderId}.", userId, orderId); }
        }

        return OrderOperationResult<CheckoutResponse>.Ok(new CheckoutResponse(
            orderId, orderCode, OrderStatusConstants.AwaitingPayment, order.SubTotal, order.ShippingFee,
            order.TotalAmount, order.TotalQuantity, expiresAt, customerEmail, items,
            $"Chọn phương thức thanh toán (COD hoặc chuyển khoản QR) trước {expiresAt:O}; quá hạn kho sẽ tự động nhả."));
    }

    // =====================================================================
    // Helpers thuần (public để unit test)
    // =====================================================================

    /// <summary>Gộp các dòng trùng (productId,size); báo lỗi nếu tổng số lượng một dòng vượt giới hạn.</summary>
    public static (List<ReserveStockItemRequest> Lines, string? Error) MergeLines(IEnumerable<(string ProductId, int Size, int Quantity)> source)
    {
        var merged = new Dictionary<(string, int), int>();
        foreach (var (productId, size, quantity) in source)
        {
            var key = (productId, size);
            merged[key] = merged.TryGetValue(key, out var q) ? q + quantity : quantity;
        }

        var over = merged.FirstOrDefault(kv => kv.Value > CheckoutValidation.MaxQuantityPerLine);
        if (over.Value > 0)
        {
            return (new List<ReserveStockItemRequest>(),
                $"Tổng số lượng của sản phẩm {over.Key.Item1} size {over.Key.Item2} vượt tối đa {CheckoutValidation.MaxQuantityPerLine}.");
        }

        return (merged.Select(kv => new ReserveStockItemRequest(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList(), null);
    }

    /// <summary>Email trong body ưu tiên hơn claim JWT; phải hợp lệ và không chứa ký tự xuống dòng.</summary>
    public static string? ResolveEmail(string? bodyEmail, string? tokenEmail)
    {
        var candidate = FirstNonBlank(bodyEmail, tokenEmail);
        if (candidate is null || candidate.Length > CheckoutValidation.MaxEmailLength) return null;
        if (candidate.Any(c => c is '\r' or '\n' or ',' or ';' or ' ')) return null;
        return MailAddress.TryCreate(candidate, out var parsed) && parsed.Address == candidate ? candidate : null;
    }

    public static string DescribeStockFailure(StockOperationResult result)
    {
        if (result.FailedItems is { Count: > 0 })
        {
            var details = string.Join("; ", result.FailedItems.Select(f =>
                $"{f.ProductName ?? f.ProductId} size {f.Size}: cần {f.RequestedQuantity}, còn {f.AvailableQuantity} ({f.Reason})"));
            return $"Không đủ hàng để đặt: {details}";
        }
        return string.IsNullOrWhiteSpace(result.Message) ? "Kho hàng từ chối giữ hàng cho đơn này." : result.Message;
    }

    private static string? FirstNonBlank(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim() : !string.IsNullOrWhiteSpace(b) ? b.Trim() : null;

    private static bool IsCatalogUnavailable(Exception ex) =>
        ex is HttpRequestException or TimeoutException or TaskCanceledException
            or Polly.CircuitBreaker.BrokenCircuitException or Polly.Timeout.TimeoutRejectedException;

    private async Task<string> GenerateUniqueOrderCodeAsync(CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            var code = OrderCodeGenerator.Generate();
            if (!await _orders.Find(o => o.OrderCode == code).AnyAsync(ct)) return code;
        }
        return OrderCodeGenerator.Generate();
    }

    private async Task TryReleaseAsync(string orderId, string reason)
    {
        try { await _catalog.ReleaseStockAsync(new ReleaseStockRequest(orderId, reason), CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "Release stock cho {OrderId} thất bại; Catalog TTL worker sẽ tự nhả sau khi hết hạn.", orderId); }
    }
}
