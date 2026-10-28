using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PaymentService.Dtos;
using PaymentService.Services;

namespace PaymentService.Controllers;

[ApiController]
[Route("api/payments")]
[Produces("application/json")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentProcessingService _paymentService;
    private readonly IConfiguration _config;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        IPaymentProcessingService paymentService,
        IConfiguration config,
        ILogger<PaymentsController> logger)
    {
        _paymentService = paymentService;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Nhánh 1: Khách hàng chọn Thanh toán khi nhận hàng (COD).
    /// Tự động lưu giao dịch COD và bắn callback sang OrderService để duyệt kho, sinh mã vận đơn WB-... và gửi Email xác nhận.
    /// </summary>
    [HttpPost("process-cod")]
    [Authorize(Roles = "Customer,Admin")]
    [ProducesResponseType(typeof(PaymentTransactionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ProcessCod([FromBody] ProcessCodRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var (success, statusCode, data, message) = await _paymentService.ProcessCodAsync(request, userId, ct);

        if (success)
        {
            return Ok(data);
        }

        return StatusCode(statusCode, new { message, transaction = data });
    }

    /// <summary>
    /// Nhánh 2 (Bước 1): Khách hàng chọn Thanh toán chuyển khoản QR (Online_QR).
    /// Sinh link ảnh mã QR chuẩn Napas247 qua VietQR.io với đúng số tiền và nội dung mã đơn ADI-...
    /// </summary>
    [HttpPost("create-qr")]
    [Authorize(Roles = "Customer,Admin")]
    [ProducesResponseType(typeof(PaymentTransactionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PaymentTransactionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateQr([FromBody] CreateQrRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var (_, statusCode, data, _) = await _paymentService.CreateQrPaymentAsync(request, userId, ct);
        return StatusCode(statusCode, data);
    }

    /// <summary>
    /// Nhánh 2 (Bước 2): Webhook nhận biến động số dư tự động từ SePay.vn.
    /// Xác thực qua header "Authorization: Apikey {SePayApiKey}" (hoặc nhập vào ô X-SePay-Api-Key khi test trên Swagger).
    /// </summary>
    [HttpPost("webhook/sepay")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SePayWebhookResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SePayWebhook(
        [FromBody] SePayWebhookPayload payload,
        [FromHeader(Name = "X-SePay-Api-Key")] string? swaggerApiKey = null,
        CancellationToken ct = default)
    {
        var configuredKey = _config["SePaySettings:ApiKey"];
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            var providedKey = ExtractSePayApiKey(Request.Headers.Authorization.ToString(), swaggerApiKey);
            if (!IsConstantTimeEqual(configuredKey, providedKey))
            {
                _logger.LogWarning("Từ chối SePay Webhook Id={Id}: Sai hoặc thiếu SePay API Key.", payload.Id);
                return Unauthorized(new { success = false, message = "Sai hoặc thiếu SePay API Key." });
            }
        }
        else
        {
            _logger.LogWarning("SePaySettings:ApiKey đang để trống. Chấp nhận webhook không xác thực (chỉ dùng cho môi trường dev).");
        }

        var (statusCode, response) = await _paymentService.HandleSePayWebhookAsync(payload, ct);
        return StatusCode(statusCode, response);
    }

    /// <summary>
    /// Tra cứu danh sách giao dịch thanh toán của một đơn hàng theo OrderId.
    /// </summary>
    [HttpGet("order/{orderId}")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<PaymentTransactionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByOrderId(string orderId, CancellationToken ct)
    {
        var list = await _paymentService.GetTransactionsByOrderIdAsync(orderId, ct);
        return Ok(list);
    }

    public static string? ExtractSePayApiKey(string? authorizationHeader, string? fallbackHeader)
    {
        if (!string.IsNullOrWhiteSpace(fallbackHeader))
        {
            return fallbackHeader.Trim();
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        var trimmed = authorizationHeader.Trim();
        if (trimmed.StartsWith("Apikey ", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed["Apikey ".Length..].Trim();
        }

        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed["Bearer ".Length..].Trim();
        }

        return trimmed;
    }

    public static bool IsConstantTimeEqual(string expected, string? actual)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
    }
}
