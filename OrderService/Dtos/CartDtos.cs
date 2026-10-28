using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using OrderService.Models;

namespace OrderService.Dtos;

// ============================================================================
// 1. CUSTOM VALIDATION ATTRIBUTES
// ============================================================================

/// <summary>
/// Thuộc tính kiểm tra tính hợp lệ của chuỗi 24 ký tự hex đại diện cho MongoDB ObjectId.
/// Kế thừa RegularExpressionAttribute để tương thích tự nhiên với Swagger/OpenAPI spec và ASP.NET Core Model Validator.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class MongoObjectIdAttribute : RegularExpressionAttribute
{
    public const string ObjectIdRegexPattern = @"^[0-9a-fA-F]{24}$";
    public const string DefaultErrorMessage = "Trường {0} phải là mã định danh MongoDB ObjectId 24 ký tự hex hợp lệ.";

    public MongoObjectIdAttribute() : base(ObjectIdRegexPattern)
    {
        ErrorMessage = DefaultErrorMessage;
    }
}

// ============================================================================
// 2. REQUEST DTOs
// ============================================================================

/// <summary>
/// Yêu cầu thêm sản phẩm (giày Adidas) vào giỏ hàng của khách hàng.
/// Mỗi thao tác thêm tương ứng với một sản phẩm (ProductId) và kích cỡ (Size) cụ thể.
/// </summary>
public record AddToCartRequest(
    [property: Required(ErrorMessage = "ProductId là bắt buộc.")]
    [property: MongoObjectId(ErrorMessage = "ProductId phải là chuỗi 24 ký tự hex hợp lệ của MongoDB ObjectId.")]
    string ProductId,

    [property: Range(30, 55, ErrorMessage = "Size giày phải từ 30 đến 55.")]
    int Size,

    [property: Range(1, 100, ErrorMessage = "Số lượng sản phẩm thêm vào giỏ phải từ 1 đến 100.")]
    int Quantity = 1,

    string? ProductName = null,
    string? ProductSku = null,
    decimal? UnitPrice = null,
    string? ThumbnailUrl = null)
{
    public AddToCartRequest() : this(string.Empty, 0, 1) { }
}

/// <summary>
/// Yêu cầu cập nhật số lượng của một mặt hàng cụ thể (xác định bởi cặp ProductId + Size trên route) trong giỏ hàng.
/// Để xóa hoàn toàn sản phẩm khỏi giỏ, client gọi DELETE /api/carts/items/{productId}/{size}.
/// </summary>
public record UpdateCartItemRequest(
    [property: Range(1, 100, ErrorMessage = "Số lượng sản phẩm phải từ 1 đến 100.")]
    int Quantity)
{
    public UpdateCartItemRequest() : this(0) { }
}

// ============================================================================
// 3. RESPONSE DTOs
// ============================================================================

/// <summary>
/// DTO đại diện cho một mục sản phẩm nằm trong giỏ hàng của khách hàng gửi về cho Client.
/// </summary>
public record CartItemDto(
    string ProductId,
    string ProductName,
    string? ProductSku,
    int Size,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string ThumbnailUrl)
{
    /// <summary>
    /// Chuyển đổi từ Domain Model CartItem sang CartItemDto.
    /// </summary>
    public static CartItemDto FromDomain(CartItem item) => new(
        ProductId: item.ProductId,
        ProductName: item.ProductName,
        ProductSku: item.ProductSku,
        Size: item.Size,
        Quantity: item.Quantity,
        UnitPrice: item.UnitPrice,
        LineTotal: item.LineTotal,
        ThumbnailUrl: item.ThumbnailUrl);
}

/// <summary>
/// DTO phản hồi toàn bộ thông tin giỏ hàng của người dùng.
/// Định dạng phản hồi khớp chuẩn yêu cầu: Items, TotalItems, SubTotal, UpdatedAt, CreatedAt.
/// </summary>
public record CartDto(
    string? Id,
    string UserId,
    List<CartItemDto> Items,
    int TotalItems,
    decimal SubTotal,
    DateTime UpdatedAt,
    DateTime? CreatedAt = null)
{
    /// <summary>
    /// Chuyển đổi từ Domain Model Cart sang CartDto.
    /// </summary>
    public static CartDto FromDomain(Cart cart) => new(
        Id: cart.Id,
        UserId: cart.UserId,
        Items: cart.Items?.Select(CartItemDto.FromDomain).ToList() ?? new List<CartItemDto>(),
        TotalItems: cart.TotalItems,
        SubTotal: cart.SubTotal,
        UpdatedAt: cart.UpdatedAt,
        CreatedAt: cart.CreatedAt);

    /// <summary>
    /// Tạo giỏ hàng rỗng mặc định khi người dùng chưa có sản phẩm nào.
    /// </summary>
    public static CartDto Empty(string userId = "") => new(
        Id: null,
        UserId: userId,
        Items: new List<CartItemDto>(),
        TotalItems: 0,
        SubTotal: 0m,
        UpdatedAt: DateTime.UtcNow,
        CreatedAt: DateTime.UtcNow);
}

/// <summary>
/// Phản hồi chuẩn cho các thao tác xóa hoặc dọn sạch giỏ hàng kèm thông báo.
/// </summary>
public record CartActionResponseDto(
    bool Success,
    string Message,
    CartDto? Cart = null);
