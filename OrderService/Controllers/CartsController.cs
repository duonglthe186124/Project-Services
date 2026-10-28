using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using OrderService.Dtos;
using OrderService.Services;

namespace OrderService.Controllers;

/// <summary>
/// Quản lý giỏ hàng mua sắm của khách hàng (Carts API).
/// Phân quyền: Chỉ cho phép tài khoản có vai trò Customer hoặc Admin.
/// Các vai trò khác (như Driver, LogisticsStaff) bị từ chối truy cập (HTTP 403 Forbidden).
/// </summary>
[ApiController]
[Route("api/carts")]
[Authorize(Roles = "Customer,Admin")]
[Produces("application/json")]
public class CartsController : ControllerBase
{
    private readonly ICartService _cartService;
    private readonly ILogger<CartsController> _logger;

    public CartsController(ICartService cartService, ILogger<CartsController> logger)
    {
        _cartService = cartService ?? throw new ArgumentNullException(nameof(cartService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Kiểm tra tính hợp lệ của mã sản phẩm (ProductId).
    /// Yêu cầu không được để trống và phải là chuỗi 24 ký tự hex hợp lệ theo chuẩn MongoDB ObjectId.
    /// </summary>
    /// <param name="productId">Chuỗi ProductId cần kiểm tra.</param>
    /// <returns>True nếu hợp lệ, ngược lại False.</returns>
    private static bool ValidateProductId(string? productId)
    {
        return !string.IsNullOrWhiteSpace(productId)
            && ObjectId.TryParse(productId.Trim(), out _);
    }

    /// <summary>
    /// Lấy giỏ hàng của người dùng hiện tại (Customer hoặc Admin).
    /// </summary>
    /// <remarks>
    /// Trích xuất định danh người dùng từ Access Token. Nếu người dùng chưa từng có giỏ hàng,
    /// hệ thống sẽ tự động khởi tạo giỏ hàng rỗng và trả về kết quả thành công.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token điều khiển hủy tác vụ.</param>
    /// <response code="200">Lấy giỏ hàng thành công.</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ / thiếu định danh người dùng.</response>
    /// <response code="403">Người dùng không có quyền truy cập giỏ hàng (ví dụ Driver, LogisticsStaff).</response>
    [HttpGet("my-cart")]
    [ProducesResponseType(typeof(CartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CartDto>> GetMyCart(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("GET /api/carts/my-cart bị từ chối: Token không chứa claim định danh UserId.");
            return Unauthorized(new { message = "Không xác định được danh tính người dùng từ token. Vui lòng đăng nhập lại." });
        }

        var cart = await _cartService.GetOrCreateCartAsync(userId, cancellationToken);
        return Ok(CartDto.FromDomain(cart));
    }

    /// <summary>
    /// Thêm sản phẩm vào giỏ hàng với kích cỡ (size) và số lượng (quantity).
    /// </summary>
    /// <remarks>
    /// - Size giày hợp lệ từ 30 đến 55.
    /// - Số lượng mua hợp lệ từ 1 đến 100.
    /// - Nếu sản phẩm cùng (ProductId, Size) đã tồn tại trong giỏ, số lượng sẽ được cộng dồn nguyên tử.
    /// </remarks>
    /// <param name="request">Dữ liệu sản phẩm, size và số lượng cần thêm.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Thêm sản phẩm thành công, trả về toàn bộ giỏ hàng mới nhất.</response>
    /// <response code="400">Dữ liệu yêu cầu không hợp lệ (size ngoài 30-55, số lượng ngoài 1-100, ProductId rỗng).</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ.</response>
    /// <response code="403">Không có quyền thao tác giỏ hàng.</response>
    /// <response code="404">Không tìm thấy sản phẩm trong hệ thống Catalog.</response>
    [HttpPost("items")]
    [ProducesResponseType(typeof(CartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CartDto>> AddItem(
        [FromBody] AddToCartRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { message = "Không xác định được danh tính người dùng từ token. Vui lòng đăng nhập lại." });
        }

        if (request == null)
        {
            return BadRequest(new { message = "Dữ liệu yêu cầu không được để trống." });
        }

        if (!ValidateProductId(request.ProductId))
        {
            return BadRequest(new { message = "ProductId không hợp lệ. Phải là chuỗi 24 ký tự hex của MongoDB ObjectId." });
        }

        if (request.Size < 30 || request.Size > 55)
        {
            return BadRequest(new { message = "Kích cỡ giày (size) không hợp lệ. Size giày phải từ 30 đến 55." });
        }

        if (request.Quantity < 1 || request.Quantity > 100)
        {
            return BadRequest(new { message = "Số lượng mua phải từ 1 đến 100." });
        }

        try
        {
            var cart = await _cartService.AddItemToCartAsync(userId, request, cancellationToken);
            return Ok(CartDto.FromDomain(cart));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning(ex, "Thêm sản phẩm vào giỏ hàng thất bại do tham số ngoài phạm vi hợp lệ.");
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Thêm sản phẩm vào giỏ hàng thất bại do tham số không hợp lệ.");
            return BadRequest(new { message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Thêm sản phẩm vào giỏ hàng thất bại do không tìm thấy sản phẩm trong Catalog.");
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Cập nhật số lượng cho một mục sản phẩm theo ProductId và Size trong giỏ hàng.
    /// </summary>
    /// <remarks>
    /// Cập nhật lại số lượng (Quantity từ 1 đến 100) của một mục (ProductId, Size) đã có trong giỏ hàng.
    /// </remarks>
    /// <param name="productId">Mã sản phẩm (24 hex characters).</param>
    /// <param name="size">Kích cỡ giày (30 đến 55).</param>
    /// <param name="request">Dữ liệu số lượng mới cần cập nhật.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Cập nhật số lượng thành công, trả về giỏ hàng mới nhất.</response>
    /// <response code="400">Tham số không hợp lệ (size ngoài 30-55, số lượng ngoài 1-100, ProductId rỗng).</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ.</response>
    /// <response code="403">Không có quyền thao tác giỏ hàng.</response>
    /// <response code="404">Không tìm thấy mục sản phẩm tương ứng trong giỏ hàng.</response>
    [HttpPut("items/{productId}/{size}")]
    [ProducesResponseType(typeof(CartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CartDto>> UpdateItemQuantity(
        [FromRoute] [MongoObjectId] string productId,
        [FromRoute] int size,
        [FromBody] UpdateCartItemRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { message = "Không xác định được danh tính người dùng từ token. Vui lòng đăng nhập lại." });
        }

        if (!ValidateProductId(productId))
        {
            return BadRequest(new { message = "ProductId không hợp lệ. Phải là chuỗi 24 ký tự hex của MongoDB ObjectId." });
        }

        if (size < 30 || size > 55)
        {
            return BadRequest(new { message = "Kích cỡ giày (size) không hợp lệ. Size giày phải từ 30 đến 55." });
        }

        if (request == null || request.Quantity < 1 || request.Quantity > 100)
        {
            return BadRequest(new { message = "Số lượng cập nhật phải từ 1 đến 100." });
        }

        try
        {
            var cart = await _cartService.UpdateItemQuantityAsync(userId, productId.Trim(), size, request.Quantity, cancellationToken);
            if (cart == null)
            {
                return NotFound(new { message = $"Không tìm thấy sản phẩm '{productId}' với size {size} trong giỏ hàng." });
            }

            return Ok(CartDto.FromDomain(cart));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Xoá một mục sản phẩm theo ProductId và Size khỏi giỏ hàng.
    /// </summary>
    /// <param name="productId">Mã sản phẩm cần xoá.</param>
    /// <param name="size">Kích cỡ giày của mục cần xoá (30 đến 55).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Xoá mục thành công, trả về giỏ hàng sau khi xoá.</response>
    /// <response code="400">Tham số không hợp lệ (size ngoài 30-55 hoặc ProductId rỗng).</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ.</response>
    /// <response code="403">Không có quyền thao tác giỏ hàng.</response>
    /// <response code="404">Không tìm thấy mục sản phẩm tương ứng trong giỏ hàng.</response>
    [HttpDelete("items/{productId}/{size}")]
    [ProducesResponseType(typeof(CartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CartDto>> RemoveItem(
        [FromRoute] [MongoObjectId] string productId,
        [FromRoute] int size,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { message = "Không xác định được danh tính người dùng từ token. Vui lòng đăng nhập lại." });
        }

        if (!ValidateProductId(productId))
        {
            return BadRequest(new { message = "ProductId không hợp lệ. Phải là chuỗi 24 ký tự hex của MongoDB ObjectId." });
        }

        if (size < 30 || size > 55)
        {
            return BadRequest(new { message = "Kích cỡ giày (size) không hợp lệ. Size giày phải từ 30 đến 55." });
        }

        try
        {
            var cart = await _cartService.RemoveItemFromCartAsync(userId, productId.Trim(), size, cancellationToken);
            if (cart == null)
            {
                return NotFound(new { message = $"Không tìm thấy sản phẩm '{productId}' với size {size} trong giỏ hàng." });
            }

            return Ok(CartDto.FromDomain(cart));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Xoá sạch toàn bộ sản phẩm trong giỏ hàng của người dùng hiện tại.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Xoá sạch giỏ hàng thành công, trả về giỏ hàng trống.</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ.</response>
    /// <response code="403">Không có quyền thao tác giỏ hàng.</response>
    [HttpDelete("clear")]
    [ProducesResponseType(typeof(CartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CartDto>> ClearCart(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { message = "Không xác định được danh tính người dùng từ token. Vui lòng đăng nhập lại." });
        }

        var cart = await _cartService.ClearCartAsync(userId, cancellationToken);
        return Ok(CartDto.FromDomain(cart));
    }

    /// <summary>
    /// Trích xuất UserId từ JWT claims.
    /// Ưu tiên ClaimTypes.NameIdentifier (khi claim mapping bật), fallback về JwtRegisteredClaimNames.Sub.
    /// </summary>
    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
    }
}
