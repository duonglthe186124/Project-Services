using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using OrderService.Models;

namespace OrderService.Services;

/// <summary>
/// Dựng nội dung email xác nhận đơn hàng (HTML + plain-text). Thuần, không I/O để dễ kiểm thử.
/// Mọi dữ liệu do người dùng nhập đều được HtmlEncode để chống chèn HTML/XSS vào email.
/// </summary>
public static class OrderEmailBuilder
{
    private static readonly NumberFormatInfo VnFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = new[] { 3 }
    };

    public static string FormatMoney(decimal amount) => amount.ToString("N0", VnFormat) + " ₫";

    public static string PaymentMethodLabel(string? method) => method switch
    {
        PaymentMethodConstants.COD => "Thanh toán khi nhận hàng (COD)",
        PaymentMethodConstants.Online_QR => "Chuyển khoản QR ngân hàng (đã thanh toán)",
        null or "" => "Chưa xác định",
        _ => method
    };

    public static string BuildSubject(Order order) =>
        $"[Adidas] Xác nhận đơn hàng {SanitizeHeader(order.OrderCode)} - Mã vận đơn {SanitizeHeader(order.WaybillCode ?? "đang cập nhật")}";

    public static string BuildPlainText(Order order)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Xin chào {order.CustomerName},");
        sb.AppendLine("Đơn hàng của bạn đã được xác nhận.");
        sb.AppendLine();
        sb.AppendLine($"Mã đơn hàng : {order.OrderCode}");
        sb.AppendLine($"Mã vận đơn  : {order.WaybillCode ?? "đang cập nhật"}");
        sb.AppendLine($"Người nhận  : {order.CustomerName}");
        sb.AppendLine($"Số điện thoại: {order.CustomerPhone}");
        sb.AppendLine($"Địa chỉ     : {FormatAddress(order)}");
        sb.AppendLine($"Thanh toán  : {PaymentMethodLabel(order.PaymentMethod)}");
        sb.AppendLine();
        sb.AppendLine("Chi tiết sản phẩm:");
        foreach (var i in order.Items)
        {
            sb.AppendLine($" - {i.ProductName} | Size {i.Size} | SL {i.Quantity} x {FormatMoney(i.UnitPrice)} = {FormatMoney(i.LineTotal)}");
        }
        sb.AppendLine();
        sb.AppendLine($"Tạm tính     : {FormatMoney(order.SubTotal)}");
        sb.AppendLine($"Phí vận chuyển: {FormatMoney(order.ShippingFee)}");
        if (order.DiscountAmount > 0) sb.AppendLine($"Giảm giá     : -{FormatMoney(order.DiscountAmount)}");
        sb.AppendLine($"TỔNG CỘNG    : {FormatMoney(order.TotalAmount)}");
        sb.AppendLine();
        sb.AppendLine("Cảm ơn bạn đã mua sắm tại Adidas.");
        return sb.ToString();
    }

    public static string BuildHtml(Order order)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

        var rows = string.Concat(order.Items.Select(i =>
            $"<tr><td style='padding:8px;border-bottom:1px solid #eee'>{E(i.ProductName)}</td>" +
            $"<td style='padding:8px;border-bottom:1px solid #eee;text-align:center'>{i.Size}</td>" +
            $"<td style='padding:8px;border-bottom:1px solid #eee;text-align:center'>{i.Quantity}</td>" +
            $"<td style='padding:8px;border-bottom:1px solid #eee;text-align:right'>{E(FormatMoney(i.UnitPrice))}</td>" +
            $"<td style='padding:8px;border-bottom:1px solid #eee;text-align:right'>{E(FormatMoney(i.LineTotal))}</td></tr>"));

        var discount = order.DiscountAmount > 0
            ? $"<p style='margin:2px 0'>Giảm giá: -{E(FormatMoney(order.DiscountAmount))}</p>"
            : string.Empty;

        return
            "<div style='font-family:Arial,Helvetica,sans-serif;max-width:640px;margin:auto;color:#111'>" +
            "<h2 style='background:#000;color:#fff;padding:16px;margin:0'>adidas - Xác nhận đơn hàng</h2>" +
            "<div style='padding:16px;border:1px solid #eee'>" +
            $"<p>Xin chào <b>{E(order.CustomerName)}</b>, đơn hàng của bạn đã được xác nhận.</p>" +
            $"<p style='margin:2px 0'>Mã đơn hàng: <b>{E(order.OrderCode)}</b></p>" +
            $"<p style='margin:2px 0'>Mã vận đơn: <b>{E(order.WaybillCode ?? "đang cập nhật")}</b></p>" +
            $"<p style='margin:2px 0'>Số điện thoại: {E(order.CustomerPhone)}</p>" +
            $"<p style='margin:2px 0'>Địa chỉ giao hàng: {E(FormatAddress(order))}</p>" +
            $"<p style='margin:2px 0'>Thanh toán: {E(PaymentMethodLabel(order.PaymentMethod))}</p>" +
            "<table style='width:100%;border-collapse:collapse;margin-top:12px'>" +
            "<tr style='background:#f5f5f5'><th style='padding:8px;text-align:left'>Sản phẩm</th><th>Size</th><th>SL</th>" +
            "<th style='text-align:right'>Đơn giá</th><th style='text-align:right'>Thành tiền</th></tr>" +
            rows + "</table>" +
            $"<p style='margin:12px 0 2px'>Tạm tính: {E(FormatMoney(order.SubTotal))}</p>" +
            $"<p style='margin:2px 0'>Phí vận chuyển: {E(FormatMoney(order.ShippingFee))}</p>" +
            discount +
            $"<p style='font-size:18px;margin:8px 0'><b>Tổng cộng: {E(FormatMoney(order.TotalAmount))}</b></p>" +
            "<p style='color:#666'>Cảm ơn bạn đã mua sắm tại Adidas.</p></div></div>";
    }

    private static string FormatAddress(Order order)
    {
        var a = order.ShippingAddress;
        return string.Join(", ", new[] { a.AddressLine, a.Ward, a.District, a.City }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>Loại bỏ CR/LF khỏi giá trị đưa vào header (chống header injection).</summary>
    private static string SanitizeHeader(string value) =>
        value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
