using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using OrderService.Data;
using OrderService.Services;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 1. Cấu hình Configuration: Nạp appsettings.Development.json khi chạy local
// ============================================================================
builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);

// ============================================================================
// 2. Kết nối MongoDB OrderDb
// ============================================================================
builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    // Ưu tiên đọc OrderDbSettings:ConnectionString, nếu trống fallback về MongoDbSettings:ConnectionString
    var connectionString = config["OrderDbSettings:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        connectionString = config["MongoDbSettings:ConnectionString"];
    }

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Chưa cấu hình ConnectionString cho MongoDB! Hãy đảm bảo cấu hình trong appsettings hoặc biến môi trường.");
    }

    var databaseName = config["OrderDbSettings:DatabaseName"];
    if (string.IsNullOrWhiteSpace(databaseName))
    {
        databaseName = "OrderDb";
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
// 4. JWT Authentication & Authorization (Stateless dùng chung Secret với AuthService)
// ============================================================================
var jwtSecret = builder.Configuration["JwtSettings:Secret"]
                ?? "Chuoi_Khoa_Bi_Mat_Cuc_Ky_Dai_It_Nhat_32_Ky_Tu_Random_123456!";

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
// 5. Controllers & Swagger với Bearer Token Support
// ============================================================================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Adidas Order Service API",
        Version = "v1",
        Description = "Microservice xử lý Giỏ hàng (Cart), Checkout, và Quản lý vòng đời Đơn hàng với In-Process OCC Saga Workflow Engine."
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
// 6. Resilient HTTP Client cho CatalogService (Polly Retry + Circuit Breaker)
// ============================================================================
builder.Services.AddHttpClient<ICatalogServiceClient, CatalogServiceClient>(client =>
{
    var baseUrl = builder.Configuration["CatalogService:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        baseUrl = "http://localhost:5000";
    }

    if (!baseUrl.EndsWith("/"))
    {
        baseUrl += "/";
    }

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
})
.AddResilienceHandler("catalog-pipeline", pipelineBuilder =>
{
    // Retry lũy thừa 3 lần (1s, 2s, 4s + jitter).
    // Không retry HTTP 409 Conflict (hết hàng hoặc phiên đã release/confirmed là logic nghiệp vụ).
    pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromSeconds(1),
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response =>
                (int)response.StatusCode >= 500 ||
                response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
    });

    // Circuit Breaker: mở mạch sau 5 lần lỗi trong 30s, giữ mạch mở 30s
    pipelineBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
    {
        SamplingDuration = TimeSpan.FromSeconds(30),
        FailureRatio = 0.5,
        MinimumThroughput = 5,
        BreakDuration = TimeSpan.FromSeconds(30),
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response => (int)response.StatusCode >= 500)
    });

    // Timeout cho mỗi lần thử HTTP
    pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(5));
});

// ============================================================================
// 6b. Named HttpClient cho Catalog Product Queries
// ============================================================================
builder.Services.AddHttpClient("CatalogClient", client =>
{
    var baseUrl = builder.Configuration["CatalogService:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        baseUrl = "http://localhost:5000";
    }

    if (!baseUrl.EndsWith("/"))
    {
        baseUrl += "/";
    }

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// ============================================================================
// 6c. Domain Services DI Registration
// ============================================================================
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IOrderSagaOrchestrator, OrderSagaOrchestrator>();
builder.Services.AddScoped<IOrderQueryService, OrderQueryService>();
builder.Services.AddScoped<IOrderCheckoutService, OrderCheckoutService>();
builder.Services.AddSingleton<IOrderEmailService, OrderEmailService>();

var app = builder.Build();

// ============================================================================
// 7. Tự động khởi tạo MongoDB Indexes khi khởi động
// ============================================================================
try
{
    await OrderDbInitializer.InitializeAsync(app.Services);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Cảnh báo: Không thể khởi tạo indexes MongoDB cho OrderService.");
}

// ============================================================================
// 8. HTTP Middleware Pipeline (Tuân thủ nghiêm ngặt thứ tự middleware)
// ============================================================================
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "OrderService v1"));

app.UseCors("AllowAll");

// Bắt buộc: UseAuthentication trước UseAuthorization
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
