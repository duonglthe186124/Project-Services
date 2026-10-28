using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderService.Models;

/// <summary>
/// Bản ghi lưu trữ đơn hàng chính thức của hệ thống (Collection "Orders").
/// Được tạo ra sau khi CatalogService xác nhận giữ kho thành công (Atomic Stock Reservation).
/// </summary>
public class Order
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>
    /// Mã đơn hàng công khai (Unique Index ux_orderCode, định dạng: ADI-yyyyMMdd-XXXX).
    /// </summary>
    [BsonElement("orderCode")]
    public string OrderCode { get; set; } = string.Empty;

    /// <summary>
    /// Mã định danh khách hàng đặt hàng (Index ix_userId_createdAt).
    /// </summary>
    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("customerName")]
    public string CustomerName { get; set; } = string.Empty;

    [BsonElement("customerPhone")]
    public string CustomerPhone { get; set; } = string.Empty;

    /// <summary>
    /// Email nhận xác nhận đơn hàng (lấy từ JWT claim "email" hoặc ghi đè trong body checkout).
    /// </summary>
    [BsonElement("customerEmail")]
    public string? CustomerEmail { get; set; }

    /// <summary>
    /// Mã vận đơn tự sinh khi thanh toán thành công / COD được ghi nhận (định dạng: WB-yyyyMMdd-XXXXXX).
    /// </summary>
    [BsonElement("waybillCode")]
    public string? WaybillCode { get; set; }

    [BsonElement("shippingAddress")]
    public ShippingAddress ShippingAddress { get; set; } = new();

    /// <summary>
    /// Danh sách chi tiết các mặt hàng trong đơn, chứa snapshot giá và hình ảnh thực tế từ Catalog.
    /// </summary>
    [BsonElement("items")]
    public List<OrderItem> Items { get; set; } = new();

    /// <summary>
    /// Tiền hàng thực tế (Zero-Trust Snapshot từ CatalogService).
    /// </summary>
    [BsonElement("subTotal")]
    public decimal SubTotal { get; set; }

    /// <summary>
    /// Phí vận chuyển (VNĐ).
    /// </summary>
    [BsonElement("shippingFee")]
    public decimal ShippingFee { get; set; } = 0m;

    /// <summary>
    /// Số tiền giảm giá khuyến mãi (VNĐ).
    /// </summary>
    [BsonElement("discountAmount")]
    public decimal DiscountAmount { get; set; } = 0m;

    /// <summary>
    /// Tổng tiền đơn hàng phải thanh toán: SubTotal + ShippingFee - DiscountAmount.
    /// </summary>
    [BsonElement("totalAmount")]
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Phương thức thanh toán: COD, Online_QR, Online_Card, EWallet.
    /// Đơn >= 5.000.000 VNĐ bắt buộc thanh toán Online 100%.
    /// </summary>
    [BsonElement("paymentMethod")]
    public string PaymentMethod { get; set; } = PaymentMethodConstants.Online_QR;

    /// <summary>
    /// Trạng thái đơn hàng (Index ix_orderStatus, đồng bộ từ OrderSagaState).
    /// </summary>
    [BsonElement("orderStatus")]
    public string OrderStatus { get; set; } = OrderStatusConstants.AwaitingPayment;

    // ==================== TIERED RISK POLICIES ====================

    /// <summary>
    /// Cờ đánh dấu đơn hàng giá trị cao (Tổng tiền >= 5.000.000 VNĐ).
    /// Bắt buộc Online 100%, tự động sinh DeliveryOtpCode và SecuritySealCode.
    /// </summary>
    [BsonElement("isHighValue")]
    public bool IsHighValue { get; set; } = false;

    /// <summary>
    /// Mã OTP 6 chữ số bàn giao cho khách hàng (dành cho đơn HighValue và VipBulk).
    /// Tài xế giao hàng phải nhập đúng OTP này mới hoàn tất giao nhận.
    /// </summary>
    [BsonElement("deliveryOtpCode")]
    public string? DeliveryOtpCode { get; set; }

    /// <summary>
    /// Mã niêm phong an ninh dạng "SEAL-xxxxxx" in trên thùng hàng trước khi xuất kho.
    /// </summary>
    [BsonElement("securitySealCode")]
    public string? SecuritySealCode { get; set; }

    /// <summary>
    /// Cờ đánh dấu đơn hàng VIP số lượng lớn (Tổng tiền >= 15.000.000 VNĐ hoặc Số lượng >= 4 đôi).
    /// </summary>
    [BsonElement("isVipBulk")]
    public bool IsVipBulk { get; set; } = false;

    /// <summary>
    /// Đơn hàng có cần Admin hoặc LogisticsStaff duyệt thủ công hay không (Bắt buộc với đơn VipBulk).
    /// </summary>
    [BsonElement("requiresManualApproval")]
    public bool RequiresManualApproval { get; set; } = false;

    /// <summary>
    /// Trạng thái đã được Admin / LogisticsStaff phê duyệt hay chưa.
    /// </summary>
    [BsonElement("isManuallyApproved")]
    public bool IsManuallyApproved { get; set; } = false;

    /// <summary>
    /// Tên tài khoản hoặc UserId của nhân sự đã duyệt đơn.
    /// </summary>
    [BsonElement("approvedBy")]
    public string? ApprovedBy { get; set; }

    /// <summary>
    /// Thời điểm phê duyệt đơn (UTC).
    /// </summary>
    [BsonElement("approvedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ApprovedAt { get; set; }

    // ==================== INTER-SERVICE CORRELATION ====================

    /// <summary>
    /// Mã định danh phiên khóa kho tương ứng trong CatalogDb.StockReservations.
    /// </summary>
    [BsonElement("reservationId")]
    public string? ReservationId { get; set; }

    /// <summary>
    /// Mã giao dịch thanh toán do PaymentService/cổng thanh toán gửi về qua internal callback (dùng chống xử lý trùng).
    /// </summary>
    [BsonElement("paymentTransactionId")]
    public string? PaymentTransactionId { get; set; }

    /// <summary>
    /// Thời điểm thanh toán thành công (UTC).
    /// </summary>
    [BsonElement("paidAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PaidAt { get; set; }

    [BsonElement("createdAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Tổng số lượng giày trong đơn hàng.
    /// </summary>
    [BsonIgnore]
    public int TotalQuantity => Items?.Sum(i => i.Quantity) ?? 0;
}
