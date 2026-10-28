using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using OrderService.Models;

namespace OrderService.Dtos;

/// <summary>Địa chỉ giao hàng trong body checkout.</summary>
public record CheckoutAddressRequest(
    [property: Required(ErrorMessage = "Địa chỉ (số nhà, tên đường) là bắt buộc.")]
    [property: StringLength(200, MinimumLength = 5, ErrorMessage = "Địa chỉ phải từ 5 đến 200 ký tự.")]
    string AddressLine,

    [property: Required(ErrorMessage = "Phường/Xã là bắt buộc.")]
    [property: StringLength(100, MinimumLength = 1, ErrorMessage = "Phường/Xã tối đa 100 ký tự.")]
    string Ward,

    [property: Required(ErrorMessage = "Quận/Huyện là bắt buộc.")]
    [property: StringLength(100, MinimumLength = 1, ErrorMessage = "Quận/Huyện tối đa 100 ký tự.")]
    string District,

    [property: Required(ErrorMessage = "Tỉnh/Thành phố là bắt buộc.")]
    [property: StringLength(100, MinimumLength = 1, ErrorMessage = "Tỉnh/Thành phố tối đa 100 ký tự.")]
    string City,

    [property: Range(-90.0, 90.0, ErrorMessage = "Latitude phải từ -90 đến 90.")]
    double? Latitude = null,

    [property: Range(-180.0, 180.0, ErrorMessage = "Longitude phải từ -180 đến 180.")]
    double? Longitude = null)
{
    public CheckoutAddressRequest() : this(string.Empty, string.Empty, string.Empty, string.Empty) { }
}

/// <summary>Một dòng hàng truyền trực tiếp trong body checkout (mua ngay, bỏ qua giỏ hàng).</summary>
public record CheckoutItemRequest(
    [property: Required(ErrorMessage = "ProductId là bắt buộc.")]
    [property: MongoObjectId]
    string ProductId,

    [property: Range(30, 55, ErrorMessage = "Size giày phải từ 30 đến 55.")]
    int Size,

    [property: Range(1, 100, ErrorMessage = "Số lượng mỗi dòng phải từ 1 đến 100.")]
    int Quantity)
{
    public CheckoutItemRequest() : this(string.Empty, 0, 0) { }
}

/// <summary>
/// Body POST /api/orders/checkout.
/// Items để trống → lấy từ giỏ hàng của người dùng; có Items → dùng danh sách này (không đụng giỏ hàng).
/// CustomerName/CustomerEmail để trống → lấy từ JWT claim "fullName"/"email".
/// </summary>
public record CheckoutRequest(
    [property: Required(ErrorMessage = "Số điện thoại người nhận là bắt buộc.")]
    [property: RegularExpression(CheckoutValidation.PhonePattern, ErrorMessage = "Số điện thoại không hợp lệ (định dạng Việt Nam, ví dụ 0912345678 hoặc +84912345678).")]
    string CustomerPhone,

    [property: Required(ErrorMessage = "Địa chỉ giao hàng là bắt buộc.")]
    CheckoutAddressRequest ShippingAddress,

    [property: StringLength(100, MinimumLength = 2, ErrorMessage = "Tên người nhận phải từ 2 đến 100 ký tự.")]
    string? CustomerName = null,

    [property: EmailAddress(ErrorMessage = "Email không hợp lệ.")]
    [property: MaxLength(254, ErrorMessage = "Email tối đa 254 ký tự.")]
    string? CustomerEmail = null,

    [property: MaxLength(50, ErrorMessage = "Mỗi đơn tối đa 50 dòng sản phẩm.")]
    List<CheckoutItemRequest>? Items = null)
{
    public CheckoutRequest() : this(string.Empty, null!) { }
}

/// <summary>Kết quả checkout: đơn đã tạo ở AwaitingPayment, khách chuyển sang bước thanh toán (PaymentService).</summary>
public record CheckoutResponse(
    string OrderId,
    string OrderCode,
    string OrderStatus,
    decimal SubTotal,
    decimal ShippingFee,
    decimal TotalAmount,
    int TotalQuantity,
    DateTime ExpiresAt,
    string? CustomerEmail,
    List<OrderItem> Items,
    string NextStep);

/// <summary>Hằng số và helper kiểm tra đầu vào checkout (thuần, dễ test).</summary>
public static class CheckoutValidation
{
    public const string PhonePattern = @"^(\+84|84|0)(3|5|7|8|9)\d{8}$";
    public const int MaxQuantityPerLine = 100;
    public const int MaxEmailLength = 254;
}
