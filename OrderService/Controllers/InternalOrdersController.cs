using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderService.Dtos;
using OrderService.Services;

namespace OrderService.Controllers;

/// <summary>
/// API nội bộ service-to-service (không dùng JWT người dùng).
/// Bảo vệ bằng header X-Internal-Api-Key so với cấu hình InternalApi:Key.
/// Nếu chưa cấu hình key (môi trường dev) thì cho qua và ghi log cảnh báo.
/// </summary>
[ApiController]
[Route("api/orders/internal")]
[AllowAnonymous]
[Produces("application/json")]
public class InternalOrdersController : ControllerBase
{
    public const string ApiKeyHeader = "X-Internal-Api-Key";

    private readonly IOrderSagaOrchestrator _orchestrator;
    private readonly IConfiguration _config;
    private readonly ILogger<InternalOrdersController> _logger;

    public InternalOrdersController(IOrderSagaOrchestrator orchestrator, IConfiguration config, ILogger<InternalOrdersController> logger)
    {
        _orchestrator = orchestrator;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Nhận kết quả thanh toán từ PaymentService.
    /// AwaitingPayment → PaymentVerified → (Catalog confirm) → ReadyForDispatch.
    /// Đơn đã Expired/Cancelled → LatePaymentDetected (không confirm kho, cần hoàn tiền).
    /// </summary>
    /// <response code="200">Xử lý xong (kể cả replay / late payment / ghi nhận thất bại).</response>
    /// <response code="202">Đã ghi nhận thanh toán, xác nhận kho đang chờ retry (Catalog lỗi mạng).</response>
    /// <response code="400">Payload sai hoặc số tiền không khớp.</response>
    /// <response code="401">Sai hoặc thiếu X-Internal-Api-Key.</response>
    /// <response code="404">Không tìm thấy đơn.</response>
    /// <response code="409">Trạng thái không cho phép.</response>
    [HttpPost("payment-callback")]
    [ProducesResponseType(typeof(PaymentCallbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PaymentCallbackResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PaymentCallback([FromBody] PaymentCallbackRequest request, CancellationToken ct)
    {
        var configuredKey = _config["InternalApi:Key"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            _logger.LogWarning("InternalApi:Key chưa được cấu hình. payment-callback đang KHÔNG được bảo vệ (chỉ chấp nhận ở môi trường dev).");
        }
        else if (!IsValidApiKey(configuredKey, Request.Headers[ApiKeyHeader].ToString()))
        {
            _logger.LogWarning("payment-callback bị từ chối: sai {Header} cho OrderId {OrderId}.", ApiKeyHeader, request.OrderId);
            return Unauthorized(new { message = "Thiếu hoặc sai internal API key." });
        }

        var result = await _orchestrator.HandlePaymentCallbackAsync(request, ct);
        return result.Kind switch
        {
            OrderResultKind.Ok => Ok(result.Value),
            OrderResultKind.Accepted => StatusCode(StatusCodes.Status202Accepted, result.Value),
            OrderResultKind.NotFound => NotFound(new { message = result.Message }),
            OrderResultKind.Conflict => Conflict(new { message = result.Message }),
            _ => BadRequest(new { message = result.Message })
        };
    }

    /// <summary>So sánh key theo thời gian hằng (chống timing attack).</summary>
    public static bool IsValidApiKey(string configuredKey, string? providedKey)
    {
        if (string.IsNullOrEmpty(providedKey)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(configuredKey), Encoding.UTF8.GetBytes(providedKey));
    }
}
