using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentService.Dtos;

/// <summary>
/// Request khi khách hàng chọn thanh toán khi nhận hàng (COD).
/// </summary>
public record ProcessCodRequest(
    [property: Required(ErrorMessage = "OrderId là bắt buộc.")]
    [property: RegularExpression("^[0-9a-fA-F]{24}$", ErrorMessage = "OrderId phải là MongoDB ObjectId 24 ký tự hex.")]
    string OrderId,

    [property: Required(ErrorMessage = "OrderCode là bắt buộc (VD: ADI-20261027-XXXXXX).")]
    [property: MaxLength(40)]
    string OrderCode,

    [property: Range(typeof(decimal), "0.01", "100000000000", ErrorMessage = "Amount phải lớn hơn 0.",
        ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    decimal Amount)
{
    public ProcessCodRequest() : this(string.Empty, string.Empty, 0m) { }
}

/// <summary>
/// Request khi khách hàng chọn thanh toán chuyển khoản Online qua mã QR (VietQR.io).
/// </summary>
public record CreateQrRequest(
    [property: Required(ErrorMessage = "OrderId là bắt buộc.")]
    [property: RegularExpression("^[0-9a-fA-F]{24}$", ErrorMessage = "OrderId phải là MongoDB ObjectId 24 ký tự hex.")]
    string OrderId,

    [property: Required(ErrorMessage = "OrderCode là bắt buộc (VD: ADI-20261027-XXXXXX).")]
    [property: MaxLength(40)]
    string OrderCode,

    [property: Range(typeof(decimal), "0.01", "100000000000", ErrorMessage = "Amount phải lớn hơn 0.",
        ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    decimal Amount)
{
    public CreateQrRequest() : this(string.Empty, string.Empty, 0m) { }
}

/// <summary>
/// Thông tin phản hồi khi khởi tạo thanh toán (COD hoặc Online_QR).
/// </summary>
public record PaymentTransactionDto(
    string TransactionId,
    string TransactionCode,
    string OrderId,
    string OrderCode,
    decimal Amount,
    string PaymentMethod,
    string Status,
    string? QrImageUrl,
    string? BankId,
    string? AccountNo,
    string? AccountName,
    string? TransferContent,
    bool CallbackSent,
    string? OrderCallbackStatus,
    string Message,
    DateTime CreatedAt,
    DateTime? PaidAt);

/// <summary>
/// Payload JSON chuẩn do SePay.vn bắn về qua Webhook khi có biến động số dư.
/// Tài liệu chuẩn SePay: https://docs.sepay.vn/tich-hop-webhooks.html
/// </summary>
public record SePayWebhookPayload(
    [property: JsonPropertyName("id")]
    long Id,

    [property: JsonPropertyName("gateway")]
    string? Gateway,

    [property: JsonPropertyName("transactionDate")]
    string? TransactionDate,

    [property: JsonPropertyName("accountNumber")]
    string? AccountNumber,

    [property: JsonPropertyName("code")]
    string? Code,

    [property: JsonPropertyName("content")]
    string? Content,

    [property: JsonPropertyName("transferType")]
    string? TransferType,

    [property: JsonPropertyName("transferAmount")]
    decimal TransferAmount,

    [property: JsonPropertyName("accumulated")]
    decimal Accumulated = 0m,

    [property: JsonPropertyName("subAccount")]
    string? SubAccount = null,

    [property: JsonPropertyName("referenceCode")]
    string? ReferenceCode = null,

    [property: JsonPropertyName("description")]
    string? Description = null)
{
    public SePayWebhookPayload() : this(0L, null, null, null, null, null, "in", 0m) { }
}

/// <summary>
/// Phản hồi trả về cho SePay Webhook (SePay yêu cầu HTTP 200/201 kèm success = true).
/// </summary>
public record SePayWebhookResponse(
    bool Success,
    string Message,
    string? OrderId = null,
    string? OrderCode = null,
    string? TransactionCode = null,
    string? OrderStatus = null);

/// <summary>
/// Payload gửi sang POST /api/orders/internal/payment-callback của OrderService.
/// </summary>
public record OrderPaymentCallbackPayload(
    string OrderId,
    string PaymentTransactionId,
    decimal Amount,
    string PaymentMethod,
    DateTime CallbackTimestamp,
    string Status = "Success");

/// <summary>
/// Kết quả trả về từ OrderService khi gọi payment-callback.
/// </summary>
public record OrderPaymentCallbackResult(
    bool HttpSuccess,
    int HttpStatusCode,
    string? OrderStatus,
    bool IsReplay,
    bool RefundRequired,
    string Message);
