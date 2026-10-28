using System;
using System.Linq;

namespace OrderService.Models;

/// <summary>
/// Chuẩn hóa các trạng thái trong vòng đời đơn hàng và Saga State Machine.
/// </summary>
public static class OrderStatusConstants
{
    /// <summary>Đơn hàng đã được tạo, tồn kho đã bị khóa ở Catalog trong 15 phút, chờ thanh toán hoặc xác nhận COD.</summary>
    public const string AwaitingPayment = "AwaitingPayment";

    /// <summary>Cổng thanh toán đã xác thực trừ tiền thành công trong thời hạn TTL 15 phút.</summary>
    public const string PaymentVerified = "PaymentVerified";

    /// <summary>Tồn kho đã được xác nhận trừ chính thức tại Catalog; đơn hàng sẵn sàng để kho đóng gói.</summary>
    public const string ReadyForDispatch = "ReadyForDispatch";

    /// <summary>Nhân viên kho đang đóng gói và dán mã niêm phong SEAL.</summary>
    public const string WarehousePacking = "WarehousePacking";

    /// <summary>Đã gán đơn cho đơn vị vận chuyển 3PL hoặc tài xế giao hàng.</summary>
    public const string CarrierAssigned = "CarrierAssigned";

    /// <summary>Đơn hàng đang trên đường giao.</summary>
    public const string InTransit = "InTransit";

    /// <summary>Đã giao hàng thành công đến tay khách hàng.</summary>
    public const string Delivered = "Delivered";

    /// <summary>Đơn hàng hoàn tất trọn vẹn (tiền đã về quỹ / COD đã đối soát).</summary>
    public const string Completed = "Completed";

    /// <summary>Đơn hàng đã bị hủy (bởi khách hàng hoặc quản trị viên), tồn kho tại Catalog đã được hoàn trả.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>Đơn hàng đã quá hạn TTL 15 phút mà không hoàn tất thanh toán, worker đã tự động nhả tồn kho.</summary>
    public const string Expired = "Expired";

    /// <summary>Thanh toán đến muộn sau khi đơn đã Expired hoặc Cancelled; tuyệt đối KHÔNG xác nhận xuất kho, đưa vào luồng hoàn tiền an toàn.</summary>
    public const string LatePaymentDetected = "LatePaymentDetected";

    public static readonly string[] All =
    [
        AwaitingPayment, PaymentVerified, ReadyForDispatch, WarehousePacking,
        CarrierAssigned, InTransit, Delivered, Completed, Cancelled, Expired, LatePaymentDetected
    ];

    public static bool IsTerminal(string? status) =>
        status is Completed or Cancelled or Expired or LatePaymentDetected;

    public static bool CanBeCancelledByCustomer(string? status) =>
        status == AwaitingPayment;
}

/// <summary>
/// Các phương thức thanh toán được hỗ trợ trong hệ thống.
/// </summary>
public static class PaymentMethodConstants
{
    public const string COD = "COD";
    public const string Online_QR = "Online_QR";
    public const string Online_Card = "Online_Card";
    public const string EWallet = "EWallet";

    public static readonly string[] All = [COD, Online_QR, Online_Card, EWallet];

    public static bool IsOnline(string? method) =>
        method is Online_QR or Online_Card or EWallet;

    public static bool IsValid(string? method) =>
        !string.IsNullOrWhiteSpace(method) &&
        All.Any(m => string.Equals(m, method.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string method) =>
        All.First(m => string.Equals(m, method.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Định nghĩa các ngưỡng chính sách kiểm soát rủi ro phân tầng và chống găm hàng.
/// </summary>
public static class OrderTierConstants
{
    public const string Standard = "Standard";
    public const string HighValue = "HighValue";
    public const string VipBulk = "VipBulk";

    /// <summary>Ngưỡng đơn giá trị cao: >= 5.000.000 VNĐ (bắt buộc Online 100%, sinh mã OTP & SEAL).</summary>
    public const decimal HighValueThresholdAmount = 5_000_000m;

    /// <summary>Ngưỡng đơn VIP số lượng lớn theo tổng tiền: >= 15.000.000 VNĐ.</summary>
    public const decimal VipBulkThresholdAmount = 15_000_000m;

    /// <summary>Ngưỡng đơn VIP số lượng lớn theo tổng số đôi: >= 4 đôi.</summary>
    public const int VipBulkThresholdQuantity = 4;

    /// <summary>Số đơn hàng AwaitingPayment tối đa mỗi khách hàng được phép giữ đồng thời (Chống găm hàng).</summary>
    public const int MaxActiveAwaitingOrdersPerUser = 2;

    /// <summary>Thời gian giữ tồn kho mặc định: 15 phút.</summary>
    public const int DefaultHoldMinutes = 15;
}

/// <summary>
/// Trạng thái của bản ghi Transactional Outbox trong OrderCompensations.
/// </summary>
public static class CompensationStatusConstants
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>
/// Tên hành động kích hoạt chuyển trạng thái Saga (Trigger Actions) phục vụ ghi AuditTrail.
/// </summary>
public static class SagaTriggerConstants
{
    public const string CheckoutSuccess = "CheckoutSuccess";
    public const string PaymentCallbackSuccess = "PaymentCallbackSuccess";
    public const string CustomerCancelled = "CustomerCancelled";
    public const string AdminCancelled = "AdminCancelled";
    public const string TimeoutExpired = "TimeoutExpired";
    public const string LatePaymentDetected = "LatePaymentDetected";
    public const string VipBulkApproved = "VipBulkApproved";
    public const string CompensationExecuted = "CompensationExecuted";
    public const string PaymentCallbackFailed = "PaymentCallbackFailed";
    public const string StockConfirmed = "StockConfirmed";
    public const string StockConfirmRejected = "StockConfirmRejected";
}
