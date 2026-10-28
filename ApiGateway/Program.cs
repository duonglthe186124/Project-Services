using System;
using System.IO;
using System.Threading.RateLimiting;
using ApiGateway.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;

// ============================================================================
// 0. Tự động nạp file .env từ thư mục gốc
// ============================================================================
LoadDotEnvFile();

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 1. Nạp cấu hình appsettings.Development.json và biến môi trường
// ============================================================================
builder.Configuration
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// ============================================================================
// 2. CORS Policy Tập Trung Quản Lý Tại Gateway
// ============================================================================
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ============================================================================
// 3. Rate Limiter (Chống DDoS / Brute-force)
// ============================================================================
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 150,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 20
        });
    });
});

// ============================================================================
// 4. Swagger UI Hợp Nhất (Gateway Aggregator)
// ============================================================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Adidas API Gateway",
        Version = "v1",
        Description = "Cửa ngõ duy nhất (Single Point of Entry) kết nối toàn bộ hệ sinh thái Microservices Adidas. Bảo vệ và chặn đứng 100% các endpoint nội bộ kho và duyệt đơn."
    });
});

// ============================================================================
// 5. Đăng ký YARP Reverse Proxy Engine
// ============================================================================
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// ============================================================================
// 6. HTTP Middleware Pipeline (Tuân thủ nghiêm ngặt thứ tự an toàn)
// ============================================================================
// 6.1 CORS
app.UseCors("AllowAll");

// 6.2 LÁ CHẮN BẢO MẬT: Chặn đứng mọi path chứa "/internal/" và strip header gian lận
app.UseInternalEndpointBlocker();

// 6.3 Rate Limiting
app.UseRateLimiter();

// 6.4 Swagger UI Hợp nhất đa dịch vụ
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "swagger";
    c.DocumentTitle = "Adidas API Gateway - Unified Swagger";
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Gateway Info v1");
    c.SwaggerEndpoint("http://localhost:5265/swagger/v1/swagger.json", "AuthService (Port 5265)");
    c.SwaggerEndpoint("http://localhost:5266/swagger/v1/swagger.json", "CatalogService (Port 5266)");
    c.SwaggerEndpoint("http://localhost:5267/swagger/v1/swagger.json", "OrderService (Port 5267)");
    c.SwaggerEndpoint("http://localhost:5268/swagger/v1/swagger.json", "PaymentService (Port 5268)");
});

// 6.5 Health Check Endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    gateway = "Adidas API Gateway (YARP .NET 8)",
    timestamp = DateTime.UtcNow,
    protectedServices = new[] { "AuthService:5265", "CatalogService:5266", "OrderService:5267", "PaymentService:5268" },
    blockedPatterns = new[] { "/api/*/internal/**", "X-Internal-Api-Key" }
}));

// 6.6 Định tuyến ngược YARP
app.MapReverseProxy();

app.Run();

// ============================================================================
// Helper: Nạp biến môi trường từ file .env
// ============================================================================
static void LoadDotEnvFile()
{
    var searchRoots = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory
    };

    foreach (var root in searchRoots)
    {
        var dir = new DirectoryInfo(root);
        while (dir is not null)
        {
            var envFile = Path.Combine(dir.FullName, ".env");
            if (File.Exists(envFile))
            {
                foreach (var rawLine in File.ReadAllLines(envFile))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    {
                        continue;
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = line[..separatorIndex].Trim();
                    var value = line[(separatorIndex + 1)..].Trim().Trim('"', '\'');

                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                }
                return;
            }
            dir = dir.Parent;
        }
    }
}
