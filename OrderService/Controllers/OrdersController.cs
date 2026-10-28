using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using OrderService.Dtos;
using OrderService.Models;
using OrderService.Services;

namespace OrderService.Controllers;

/// <summary>
/// API đơn hàng cho khách hàng và nhân sự (xem danh sách, chi tiết, timeline, hủy đơn).
/// Tenant isolation: khách hàng chỉ thấy đơn của mình; truy cập đơn người khác trả 404.
/// </summary>
[ApiController]
[Route("api/orders")]
[Authorize]
[Produces("application/json")]
public class OrdersController : ControllerBase
{
    private const string RoleAdmin = "Admin";
    private const string RoleLogistics = "LogisticsStaff";

    private readonly IOrderQueryService _query;
    private readonly IOrderSagaOrchestrator _orchestrator;
    private readonly IOrderCheckoutService _checkout;

    public OrdersController(IOrderQueryService query, IOrderSagaOrchestrator orchestrator, IOrderCheckoutService checkout)
    {
        _query = query;
        _orchestrator = orchestrator;
        _checkout = checkout;
    }

    /// <summary>
    /// Đặt hàng: lấy sản phẩm từ giỏ hàng (hoặc body.items nếu có), khóa kho 15 phút, tạo đơn AwaitingPayment.
    /// Body: customerPhone, shippingAddress{addressLine,ward,district,city}, tùy chọn customerName, customerEmail, items[].
    /// Email xác nhận chỉ được gửi sau khi thanh toán/COD thành công (callback từ PaymentService).
    /// </summary>
    [HttpPost("checkout")]
    [Authorize(Roles = "Customer,Admin")]
    [ProducesResponseType(typeof(CheckoutResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return UnauthorizedNoIdentity();

        var email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue(JwtRegisteredClaimNames.Email);
        var fullName = User.FindFirstValue("fullName");

        var result = await _checkout.CheckoutAsync(userId, email, fullName, request, ct);
        return result.Kind switch
        {
            OrderResultKind.Ok => Created($"/api/orders/{result.Value!.OrderId}", result.Value),
            OrderResultKind.Conflict => Conflict(new { message = result.Message }),
            OrderResultKind.ServiceUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = result.Message }),
            _ => BadRequest(new { message = result.Message })
        };
    }

    /// <summary>Danh sách đơn của người dùng hiện tại (mới nhất trước). Query: page (≥1), pageSize (1–50).</summary>
    [HttpGet("my-orders")]
    [Authorize(Roles = "Customer,Admin")]
    [ProducesResponseType(typeof(PagedResult<OrderSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyOrders([FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return UnauthorizedNoIdentity();
        return Ok(await _query.GetByUserAsync(userId, page, pageSize, ct));
    }

    /// <summary>Danh sách toàn bộ đơn (Admin/LogisticsStaff). Query: status (tùy chọn), page, pageSize.</summary>
    [HttpGet]
    [Authorize(Roles = "Admin,LogisticsStaff")]
    [ProducesResponseType(typeof(PagedResult<OrderSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAll([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(status) && Array.IndexOf(OrderStatusConstants.All, status.Trim()) < 0)
        {
            return BadRequest(new { message = $"Status không hợp lệ. Giá trị cho phép: {string.Join(", ", OrderStatusConstants.All)}." });
        }
        return Ok(await _query.GetAllAsync(status, page, pageSize, ct));
    }

    /// <summary>Chi tiết đơn (chủ đơn hoặc Admin/LogisticsStaff). Path: id (ObjectId).</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(OrderDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest(new { message = "OrderId không hợp lệ (phải là MongoDB ObjectId 24 ký tự hex)." });
        var userId = GetCurrentUserId();
        var (order, saga) = await _query.GetWithSagaAsync(id, ct);
        var isAdmin = User.IsInRole(RoleAdmin);
        var isLogistics = User.IsInRole(RoleLogistics);
        if (order is null || !OrderAccessPolicy.CanView(order.UserId, userId, isAdmin, isLogistics))
        {
            return NotFound(new { message = "Không tìm thấy đơn hàng." });
        }

        var isOwner = string.Equals(order.UserId, userId, StringComparison.Ordinal);
        return Ok(OrderDetailDto.FromDomain(order, saga, isOwner, OrderAccessPolicy.IsStaff(isAdmin, isLogistics)));
    }

    /// <summary>Timeline chuyển trạng thái (AuditTrail). Path: id.</summary>
    [HttpGet("{id}/timeline")]
    [ProducesResponseType(typeof(OrderTimelineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTimeline(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest(new { message = "OrderId không hợp lệ." });
        var (order, saga) = await _query.GetWithSagaAsync(id, ct);
        if (order is null || saga is null ||
            !OrderAccessPolicy.CanView(order.UserId, GetCurrentUserId(), User.IsInRole(RoleAdmin), User.IsInRole(RoleLogistics)))
        {
            return NotFound(new { message = "Không tìm thấy đơn hàng." });
        }
        return Ok(OrderTimelineDto.FromDomain(saga));
    }

    /// <summary>Hủy đơn khi còn AwaitingPayment (chủ đơn hoặc Admin). Path: id. Body: { reason }.</summary>
    [HttpPost("{id}/cancel")]
    [Authorize(Roles = "Customer,Admin")]
    [ProducesResponseType(typeof(CancelOrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(string id, [FromBody] CancelOrderRequest? request, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _)) return BadRequest(new { message = "OrderId không hợp lệ." });
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return UnauthorizedNoIdentity();

        var result = await _orchestrator.CancelAsync(id, userId, User.IsInRole(RoleAdmin), request?.Reason, ct);
        return result.Kind switch
        {
            OrderResultKind.Ok => Ok(result.Value),
            OrderResultKind.NotFound => NotFound(new { message = result.Message }),
            OrderResultKind.Conflict => Conflict(new { message = result.Message }),
            _ => BadRequest(new { message = result.Message })
        };
    }

    private IActionResult UnauthorizedNoIdentity() =>
        Unauthorized(new { message = "Không xác định được danh tính người dùng từ token. Vui lòng đăng nhập lại." });

    private string? GetCurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
}
