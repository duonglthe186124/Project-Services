using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using PaymentService.Data;
using PaymentService.Dtos;
using PaymentService.Models;

namespace PaymentService.Services;

public interface IPaymentProcessingService
{
    Task<(bool Success, int StatusCode, PaymentTransactionDto? Data, string Message)> ProcessCodAsync(
        ProcessCodRequest request, string? userId, CancellationToken ct = default);

    Task<(bool Success, int StatusCode, PaymentTransactionDto? Data, string Message)> CreateQrPaymentAsync(
        CreateQrRequest request, string? userId, CancellationToken ct = default);

    Task<(int StatusCode, SePayWebhookResponse Response)> HandleSePayWebhookAsync(
        SePayWebhookPayload payload, CancellationToken ct = default);

    Task<IReadOnlyList<PaymentTransactionDto>> GetTransactionsByOrderIdAsync(
        string orderId, CancellationToken ct = default);
}

public class PaymentProcessingService : IPaymentProcessingService
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly IMongoCollection<PaymentTransaction> _transactions;
    private readonly IVietQrService _vietQr;
    private readonly IOrderCallbackClient _orderCallback;
    private readonly ILogger<PaymentProcessingService> _logger;

    public PaymentProcessingService(
        IMongoDatabase database,
        IVietQrService vietQr,
        IOrderCallbackClient orderCallback,
        ILogger<PaymentProcessingService> logger)
    {
        _transactions = database.GetCollection<PaymentTransaction>(PaymentDbInitializer.PaymentTransactionsCollectionName);
        _vietQr = vietQr;
        _orderCallback = orderCallback;
        _logger = logger;
    }

    public async Task<(bool Success, int StatusCode, PaymentTransactionDto? Data, string Message)> ProcessCodAsync(
        ProcessCodRequest request, string? userId, CancellationToken ct = default)
    {
        var normalizedOrderCode = _vietQr.ExtractNormalizedOrderCode(request.OrderCode) ?? request.OrderCode.Trim().ToUpperInvariant();
        var now = DateTime.UtcNow;

        // Kiểm tra xem đơn này đã có giao dịch COD trước đó chưa (Idempotency)
        var existing = await _transactions
            .Find(t => t.OrderId == request.OrderId && t.PaymentMethod == PaymentMethodTypes.Cod)
            .SortByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        PaymentTransaction tx;
        if (existing is not null)
        {
            tx = existing;
        }
        else
        {
            tx = new PaymentTransaction
            {
                TransactionCode = GenerateTransactionCode("COD"),
                OrderId = request.OrderId,
                OrderCode = normalizedOrderCode,
                UserId = userId,
                Amount = request.Amount,
                PaymentMethod = PaymentMethodTypes.Cod,
                Status = PaymentStatusConstants.Pending,
                TransferContent = $"COD {normalizedOrderCode}",
                CreatedAt = now,
                UpdatedAt = now
            };
            await _transactions.InsertOneAsync(tx, cancellationToken: ct);
        }

        // Gọi ngay sang OrderService để xác nhận đơn hàng COD -> trừ kho -> sinh mã vận đơn -> gửi Email
        var callbackPayload = new OrderPaymentCallbackPayload(
            OrderId: tx.OrderId,
            PaymentTransactionId: tx.TransactionCode,
            Amount: tx.Amount,
            PaymentMethod: PaymentMethodTypes.Cod,
            CallbackTimestamp: now,
            Status: "Success");

        var callbackResult = await _orderCallback.SendPaymentCallbackAsync(callbackPayload, ct);

        tx.Status = callbackResult.HttpSuccess ? PaymentStatusConstants.Success : PaymentStatusConstants.Failed;
        tx.CallbackSent = callbackResult.HttpSuccess;
        tx.OrderCallbackStatus = callbackResult.OrderStatus;
        tx.PaidAt = callbackResult.HttpSuccess ? now : null;
        tx.UpdatedAt = DateTime.UtcNow;

        await _transactions.ReplaceOneAsync(t => t.Id == tx.Id, tx, cancellationToken: ct);

        var dto = MapToDto(tx, callbackResult.Message);

        if (!callbackResult.HttpSuccess)
        {
            return (false, callbackResult.HttpStatusCode, dto, callbackResult.Message);
        }

        return (true, 200, dto, callbackResult.Message);
    }

    public async Task<(bool Success, int StatusCode, PaymentTransactionDto? Data, string Message)> CreateQrPaymentAsync(
        CreateQrRequest request, string? userId, CancellationToken ct = default)
    {
        var normalizedOrderCode = _vietQr.ExtractNormalizedOrderCode(request.OrderCode) ?? request.OrderCode.Trim().ToUpperInvariant();
        var qrUrl = _vietQr.GenerateQrImageUrl(request.Amount, normalizedOrderCode);
        var now = DateTime.UtcNow;

        // Nếu đã có giao dịch Online_QR đang Pending cho OrderId này thì cập nhật lại và trả về
        var existing = await _transactions
            .Find(t => t.OrderId == request.OrderId && t.PaymentMethod == PaymentMethodTypes.OnlineQr)
            .SortByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            if (existing.Status == PaymentStatusConstants.Success)
            {
                var paidDto = MapToDto(existing, "Đơn hàng này đã được thanh toán thành công trước đó.");
                return (true, 200, paidDto, paidDto.Message);
            }

            existing.OrderCode = normalizedOrderCode;
            existing.Amount = request.Amount;
            existing.QrImageUrl = qrUrl;
            existing.TransferContent = normalizedOrderCode;
            existing.UpdatedAt = now;

            await _transactions.ReplaceOneAsync(t => t.Id == existing.Id, existing, cancellationToken: ct);
            var updatedDto = MapToDto(existing, "Đã tạo mã QR thanh toán VietQR. Vui lòng quét mã và giữ nguyên nội dung chuyển khoản.");
            return (true, 200, updatedDto, updatedDto.Message);
        }

        var tx = new PaymentTransaction
        {
            TransactionCode = GenerateTransactionCode("QR"),
            OrderId = request.OrderId,
            OrderCode = normalizedOrderCode,
            UserId = userId,
            Amount = request.Amount,
            PaymentMethod = PaymentMethodTypes.OnlineQr,
            Status = PaymentStatusConstants.Pending,
            QrImageUrl = qrUrl,
            TransferContent = normalizedOrderCode,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _transactions.InsertOneAsync(tx, cancellationToken: ct);

        var dto = MapToDto(tx, "Đã tạo mã QR thanh toán VietQR. Vui lòng quét mã và giữ nguyên nội dung chuyển khoản.");
        return (true, 201, dto, dto.Message);
    }

    public async Task<(int StatusCode, SePayWebhookResponse Response)> HandleSePayWebhookAsync(
        SePayWebhookPayload payload, CancellationToken ct = default)
    {
        // 1. Chỉ xử lý giao dịch tiền vào (transferType == "in")
        if (!string.IsNullOrWhiteSpace(payload.TransferType) &&
            !string.Equals(payload.TransferType.Trim(), "in", StringComparison.OrdinalIgnoreCase))
        {
            return (200, new SePayWebhookResponse(true, "Bỏ qua giao dịch tiền ra (transferType != 'in')."));
        }

        // 2. Kiểm tra chống trùng theo SePayTransactionId
        if (payload.Id > 0)
        {
            var duplicate = await _transactions
                .Find(t => t.SePayTransactionId == payload.Id)
                .FirstOrDefaultAsync(ct);

            if (duplicate is not null && duplicate.CallbackSent)
            {
                return (200, new SePayWebhookResponse(
                    true,
                    "Giao dịch SePay này đã được xử lý trước đó (Idempotent replay).",
                    duplicate.OrderId,
                    duplicate.OrderCode,
                    duplicate.TransactionCode,
                    duplicate.OrderCallbackStatus));
            }
        }

        // 3. Trích xuất OrderCode (ADI-yyyyMMdd-XXXXXX) từ content / description / code
        var orderCode = _vietQr.ExtractNormalizedOrderCode(payload.Content)
                        ?? _vietQr.ExtractNormalizedOrderCode(payload.Description)
                        ?? _vietQr.ExtractNormalizedOrderCode(payload.Code);

        if (string.IsNullOrWhiteSpace(orderCode))
        {
            _logger.LogInformation("Webhook SePay Id={Id} không chứa mã đơn ADI-... trong nội dung: '{Content}'. Bỏ qua.",
                payload.Id, payload.Content);
            return (200, new SePayWebhookResponse(true, "Giao dịch không chứa mã đơn hàng ADI-..., bỏ qua."));
        }

        // 4. Tìm phiên thanh toán tương ứng theo OrderCode
        var tx = await _transactions
            .Find(t => t.OrderCode == orderCode)
            .SortByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (tx is null)
        {
            _logger.LogWarning("Webhook SePay Id={Id}: Nhận tiền cho mã đơn {OrderCode} nhưng chưa có bản ghi trong PaymentDb.",
                payload.Id, orderCode);
            return (200, new SePayWebhookResponse(false, $"Không tìm thấy giao dịch chờ thanh toán nào cho mã đơn {orderCode}.", null, orderCode));
        }

        // 5. Đối chiếu số tiền chuyển khoản
        if (payload.TransferAmount < tx.Amount)
        {
            _logger.LogWarning("Webhook SePay Id={Id} cho đơn {OrderCode}: Khách chuyển {TransferAmount} < {RequiredAmount}.",
                payload.Id, orderCode, payload.TransferAmount, tx.Amount);
            return (200, new SePayWebhookResponse(
                false,
                $"Số tiền chuyển khoản ({payload.TransferAmount:N0}đ) thấp hơn tổng tiền đơn hàng ({tx.Amount:N0}đ).",
                tx.OrderId,
                tx.OrderCode,
                tx.TransactionCode));
        }

        // 6. Cập nhật thông tin giao dịch từ SePay và bắn callback sang OrderService
        var now = DateTime.UtcNow;
        if (payload.Id > 0)
        {
            tx.SePayTransactionId = payload.Id;
        }
        tx.Gateway = payload.Gateway;
        tx.ReferenceCode = payload.ReferenceCode;
        tx.TransferContent = payload.Content;
        tx.Status = PaymentStatusConstants.Success;
        tx.PaidAt = now;
        tx.UpdatedAt = now;

        var callbackPayload = new OrderPaymentCallbackPayload(
            OrderId: tx.OrderId,
            PaymentTransactionId: tx.TransactionCode,
            Amount: tx.Amount,
            PaymentMethod: PaymentMethodTypes.OnlineQr,
            CallbackTimestamp: now,
            Status: "Success");

        var callbackResult = await _orderCallback.SendPaymentCallbackAsync(callbackPayload, ct);

        tx.CallbackSent = callbackResult.HttpSuccess;
        tx.OrderCallbackStatus = callbackResult.OrderStatus;
        tx.UpdatedAt = DateTime.UtcNow;

        await _transactions.ReplaceOneAsync(t => t.Id == tx.Id, tx, cancellationToken: ct);

        if (!callbackResult.HttpSuccess && callbackResult.HttpStatusCode >= 500)
        {
            // Trả 503 để SePay tự động retry webhook nếu OrderService tạm thời mất kết nối
            return (503, new SePayWebhookResponse(
                false,
                callbackResult.Message,
                tx.OrderId,
                tx.OrderCode,
                tx.TransactionCode,
                callbackResult.OrderStatus));
        }

        return (200, new SePayWebhookResponse(
            callbackResult.HttpSuccess,
            callbackResult.Message,
            tx.OrderId,
            tx.OrderCode,
            tx.TransactionCode,
            callbackResult.OrderStatus));
    }

    public async Task<IReadOnlyList<PaymentTransactionDto>> GetTransactionsByOrderIdAsync(
        string orderId, CancellationToken ct = default)
    {
        var list = await _transactions
            .Find(t => t.OrderId == orderId)
            .SortByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        var result = new List<PaymentTransactionDto>(list.Count);
        foreach (var tx in list)
        {
            result.Add(MapToDto(tx, tx.Status == PaymentStatusConstants.Success
                ? "Đã thanh toán thành công."
                : "Đang chờ thanh toán."));
        }

        return result;
    }

    private PaymentTransactionDto MapToDto(PaymentTransaction tx, string message)
    {
        var isQr = tx.PaymentMethod == PaymentMethodTypes.OnlineQr;
        return new PaymentTransactionDto(
            TransactionId: tx.Id ?? string.Empty,
            TransactionCode: tx.TransactionCode,
            OrderId: tx.OrderId,
            OrderCode: tx.OrderCode,
            Amount: tx.Amount,
            PaymentMethod: tx.PaymentMethod,
            Status: tx.Status,
            QrImageUrl: tx.QrImageUrl,
            BankId: isQr ? _vietQr.BankId : null,
            AccountNo: isQr ? _vietQr.AccountNo : null,
            AccountName: isQr ? _vietQr.AccountName : null,
            TransferContent: tx.TransferContent,
            CallbackSent: tx.CallbackSent,
            OrderCallbackStatus: tx.OrderCallbackStatus,
            Message: message,
            CreatedAt: tx.CreatedAt,
            PaidAt: tx.PaidAt);
    }

    private static string GenerateTransactionCode(string prefix)
    {
        var date = DateTime.UtcNow.ToString("yyyyMMdd");
        Span<char> suffix = stackalloc char[6];
        for (int i = 0; i < suffix.Length; i++)
        {
            suffix[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return $"PAY-{prefix}-{date}-{new string(suffix)}";
    }
}
