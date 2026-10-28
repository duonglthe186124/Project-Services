using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using PaymentService.Data;
using PaymentService.Services;
using Polly;
using Polly.Retry;
using Polly.Timeout;

// ============================================================================
// 0. Tự động nạp biến môi trường từ file .env (ở thư mục gốc hoặc thư mục hiện tại)
// ============================================================================
LoadDotEnvFile();

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 1. Nạp cấu hình và ưu tiên Biến môi trường (.env) cao nhất
// ============================================================================
builder.Configuration
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// ============================================================================
// 2. Kết nối MongoDB PaymentDb (Đọc từ biến môi trường PaymentDbSettings__ConnectionString)
// ============================================================================
builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    var connectionString = config["PaymentDbSettings:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        connectionString = config["MongoDbSettings:ConnectionString"];
    }

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Chưa cấu hình PaymentDbSettings__ConnectionString trong file môi trường (.env)!");
    }

    var databaseName = config["PaymentDbSettings:DatabaseName"];
    if (string.IsNullOrWhiteSpace(databaseName))
    {
        databaseName = "PaymentDb";
    }

    var client = new MongoClient(connectionString);
    return client.GetDatabase(databaseName);
});

// ============================================================================
// 3. CORS Policy
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
// 4. JWT Authentication & Authorization (Đọc từ JwtSettings__Secret trong .env)
// ============================================================================
var jwtSecret = builder.Configuration["JwtSettings:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    throw new InvalidOperationException("Chưa cấu hình JwtSettings__Secret trong file môi trường (.env)!");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["JwtSettings:Issuer"] ?? "AuthService",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["JwtSettings:Audience"] ?? "AuthServiceUsers",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();

// ============================================================================
// 5. Controllers & Swagger UI với Bearer Token Support
// ============================================================================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Adidas Payment Service API",
        Version = "v1",
        Description = "Microservice xử lý Thanh toán COD, Sinh mã QR chuẩn Napas247 (VietQR.io) và Nhận Webhook biến động số dư tự động từ SePay.vn."
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Dán Access Token nhận được từ AuthService (không cần gõ 'Bearer ')"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ============================================================================
// 6. Resilient HttpClient gọi sang OrderService (Polly Retry 5xx / 408 / 409)
// ============================================================================
builder.Services.AddHttpClient<IOrderCallbackClient, OrderCallbackClient>(client =>
{
    var baseUrl = builder.Configuration["OrderService:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        baseUrl = "http://localhost:5267";
    }

    if (!baseUrl.EndsWith("/"))
    {
        baseUrl += "/";
    }

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddResilienceHandler("order-callback-pipeline", pipelineBuilder =>
{
    pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromMilliseconds(500),
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response =>
                (int)response.StatusCode >= 500 ||
                response.StatusCode == HttpStatusCode.RequestTimeout ||
                response.StatusCode == HttpStatusCode.Conflict)
    });

    pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(20));
});

// ============================================================================
// 7. Đăng ký Domain Services
// ============================================================================
builder.Services.AddSingleton<IVietQrService, VietQrService>();
builder.Services.AddScoped<IPaymentProcessingService, PaymentProcessingService>();

var app = builder.Build();

// ============================================================================
// 8. Tự động khởi tạo MongoDB Indexes cho PaymentDb khi khởi động
// ============================================================================
try
{
    await PaymentDbInitializer.InitializeAsync(app.Services);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Cảnh báo: Không thể khởi tạo indexes MongoDB cho PaymentService.");
}

// ============================================================================
// 9. HTTP Middleware Pipeline
// ============================================================================
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "PaymentService v1"));

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// ============================================================================
// Helper: Tự động tìm và nạp file .env từ thư mục dự án hoặc thư mục gốc Solution
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

                    // Không ghi đè nếu biến môi trường hệ thống/Docker đã được truyền vào từ bên ngoài
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
