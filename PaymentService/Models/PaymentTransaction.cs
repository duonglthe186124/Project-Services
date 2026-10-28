using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PaymentService.Models;

/// <summary>
/// Hằng số trạng thái giao dịch thanh toán.
/// </summary>
public static class PaymentStatusConstants
{
    public const string Pending = "Pending";
    public const string Success = "Success";
    public const string Failed = "Failed";
}

/// <summary>
/// Hằng số phương thức thanh toán hỗ trợ bởi PaymentService (khớp với OrderService).
/// </summary>
public static class PaymentMethodTypes
{
    public const string Cod = "COD";
    public const string OnlineQr = "Online_QR";
}

/// <summary>
/// Bản ghi giao dịch thanh toán lưu trong collection "PaymentTransactions" thuộc database "PaymentDb".
/// </summary>
[BsonIgnoreExtraElements]
public class PaymentTransaction
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// Mã giao dịch duy nhất phía PaymentService (VD: PAY-20261027-XXXXXX hoặc SEPAY-123456).
    /// Dùng làm PaymentTransactionId gửi sang OrderService để đảm bảo tính Idempotent.
    /// </summary>
    [BsonElement("transactionCode")]
    public string TransactionCode { get; set; } = string.Empty;

    /// <summary>
    /// ID đơn hàng bên OrderService (24-hex ObjectId).
    /// </summary>
    [BsonElement("orderId")]
    public string OrderId { get; set; } = string.Empty;

    /// <summary>
    /// Mã đơn hàng chuẩn Adidas (VD: ADI-20261027-ENGAW2).
    /// Dùng để đối chiếu từ nội dung chuyển khoản ngân hàng qua SePay Webhook.
    /// </summary>
    [BsonElement("orderCode")]
    public string OrderCode { get; set; } = string.Empty;

    /// <summary>
    /// ID người dùng thực hiện thanh toán (nếu tạo từ phiên đăng nhập JWT).
    /// </summary>
    [BsonElement("userId")]
    public string? UserId { get; set; }

    /// <summary>
    /// Số tiền cần thanh toán hoặc đã chuyển khoản (VNĐ).
    /// </summary>
    [BsonElement("amount")]
    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Amount { get; set; }

    /// <summary>
    /// Phương thức thanh toán: "COD" hoặc "Online_QR".
    /// </summary>
    [BsonElement("paymentMethod")]
    public string PaymentMethod { get; set; } = PaymentMethodTypes.OnlineQr;

    /// <summary>
    /// Trạng thái giao dịch: "Pending", "Success", "Failed".
    /// </summary>
    [BsonElement("status")]
    public string Status { get; set; } = PaymentStatusConstants.Pending;

    /// <summary>
    /// Đường dẫn ảnh QR chuẩn VietQR.io (chỉ có khi PaymentMethod = Online_QR).
    /// </summary>
    [BsonElement("qrImageUrl")]
    [BsonIgnoreIfNull]
    public string? QrImageUrl { get; set; }

    /// <summary>
    /// ID giao dịch từ SePay.vn (dùng để chống xử lý trùng lặp khi SePay bắn lại webhook).
    /// </summary>
    [BsonElement("sePayTransactionId")]
    [BsonIgnoreIfNull]
    public long? SePayTransactionId { get; set; }

    /// <summary>
    /// Tên cổng / ngân hàng nhận tiền từ SePay (VD: MBBank, Vietcombank).
    /// </summary>
    [BsonElement("gateway")]
    [BsonIgnoreIfNull]
    public string? Gateway { get; set; }

    /// <summary>
    /// Mã tham chiếu ngân hàng (FT code) từ SePay.
    /// </summary>
    [BsonElement("referenceCode")]
    [BsonIgnoreIfNull]
    public string? ReferenceCode { get; set; }

    /// <summary>
    /// Nội dung chuyển khoản gốc nhận từ ngân hàng / SePay.
    /// </summary>
    [BsonElement("transferContent")]
    [BsonIgnoreIfNull]
    public string? TransferContent { get; set; }

    /// <summary>
    /// Đã gọi callback sang OrderService thành công hay chưa.
    /// </summary>
    [BsonElement("callbackSent")]
    public bool CallbackSent { get; set; }

    /// <summary>
    /// Trạng thái đơn hàng trả về từ OrderService sau khi gọi callback (VD: ReadyForDispatch, LatePaymentDetected).
    /// </summary>
    [BsonElement("orderCallbackStatus")]
    [BsonIgnoreIfNull]
    public string? OrderCallbackStatus { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("paidAt")]
    [BsonIgnoreIfNull]
    public DateTime? PaidAt { get; set; }
}
