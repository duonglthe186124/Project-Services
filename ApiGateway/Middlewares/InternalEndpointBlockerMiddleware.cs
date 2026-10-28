using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Middlewares;

/// <summary>
/// Lá chắn bảo mật cấp Gateway (Defense-in-Depth):
/// 1. Chặn đứng 100% mọi request từ bên ngoài Internet gọi vào các endpoint nội bộ chứa segment "/internal/".
///    Trả về HTTP 404 Not Found (Zero-Trust) để đánh lừa kẻ tấn công như thể endpoint không hề tồn tại.
/// 2. Bóc gỡ (strip) toàn bộ header nhạy cảm gian lận như "X-Internal-Api-Key" nếu client gửi lên.
/// </summary>
public class InternalEndpointBlockerMiddleware
{
    public const string InternalApiKeyHeader = "X-Internal-Api-Key";

    private readonly RequestDelegate _next;
    private readonly ILogger<InternalEndpointBlockerMiddleware> _logger;

    private static readonly byte[] NotFoundJsonResponse = JsonSerializer.SerializeToUtf8Bytes(new
    {
        message = "Endpoint not found."
    });

    public InternalEndpointBlockerMiddleware(RequestDelegate _next, ILogger<InternalEndpointBlockerMiddleware> logger)
    {
        this._next = _next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // 1. Kiểm tra nếu request cố tình truy cập vào các API nội bộ
        if (IsInternalPath(path))
        {
            _logger.LogWarning("[SECURITY-ALERT] Chặn truy cập trái phép vào endpoint nội bộ: {Method} {Path} từ IP: {RemoteIp}",
                context.Request.Method, path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(NotFoundJsonResponse);
            return;
        }

        // 2. Chống giả mạo Header: Tẩy sạch header X-Internal-Api-Key nếu client gửi từ bên ngoài
        if (context.Request.Headers.ContainsKey(InternalApiKeyHeader))
        {
            _logger.LogWarning("[SECURITY-ALERT] Phát hiện và bóc gỡ header giả mạo {Header} từ IP: {RemoteIp}",
                InternalApiKeyHeader, context.Connection.RemoteIpAddress);
            context.Request.Headers.Remove(InternalApiKeyHeader);
        }

        await _next(context);
    }

    public static bool IsInternalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.Contains("/internal/", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/internal", StringComparison.OrdinalIgnoreCase);
    }
}

public static class InternalEndpointBlockerExtensions
{
    public static IApplicationBuilder UseInternalEndpointBlocker(this IApplicationBuilder app)
    {
        return app.UseMiddleware<InternalEndpointBlockerMiddleware>();
    }
}
