using System.Text;
using CatalogService.Data;
using CatalogService.Models;
using CatalogService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Luôn nạp appsettings.Development.json (nếu có trên máy local) để đọc cấu hình bí mật khi dev
builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);

// 1. Dependency Injection
builder.Services.AddScoped<ICatalogService, ProductCatalogService>();
builder.Services.AddHostedService<StockReservationExpirationWorker>();

// 2. CORS (Cho phép kết nối tự do khi dev)
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

// 3. JWT Authentication (Sử dụng chung Secret với AuthService để giải mã quyền Role)
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
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// 4. Cấu hình Swagger kèm nút Authorize để test quyền Customer / Admin
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Adidas Shoe Catalog Service API",
        Version = "v1",
        Description = "Microservice trưng bày, tra cứu danh mục giày thể thao Adidas và điều phối khóa tồn kho nguyên tử (Atomic Stock Reservation)."
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

// 5. Kết nối MongoDB CatalogDb
builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config["CatalogDbSettings:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
        connectionString = config["MongoDbSettings:ConnectionString"];

    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Chưa cấu hình ConnectionString cho MongoDB! Hãy đảm bảo chạy với ASPNETCORE_ENVIRONMENT=Development hoặc cấu hình trong .env.");

    var databaseName = config["CatalogDbSettings:DatabaseName"];
    if (string.IsNullOrWhiteSpace(databaseName)) databaseName = "CatalogDb";

    var client = new MongoClient(connectionString);
    return client.GetDatabase(databaseName);
});

var app = builder.Build();

// 6. Tự động khởi tạo Index & Seed dữ liệu mẫu khi khởi động
try
{
    var db = app.Services.GetRequiredService<IMongoDatabase>();
    var productsCollection = db.GetCollection<ShoeProduct>(ProductCatalogService.ProductsCollectionName);
    var reservationsCollection = db.GetCollection<StockReservation>(ProductCatalogService.StockReservationsCollectionName);

    // Index cho tìm kiếm và lọc nhanh trên bảng Products
    productsCollection.Indexes.CreateMany(new[]
    {
        new CreateIndexModel<ShoeProduct>(Builders<ShoeProduct>.IndexKeys.Ascending(p => p.Category), new CreateIndexOptions { Name = "ix_category" }),
        new CreateIndexModel<ShoeProduct>(Builders<ShoeProduct>.IndexKeys.Ascending(p => p.Price), new CreateIndexOptions { Name = "ix_price" }),
        new CreateIndexModel<ShoeProduct>(Builders<ShoeProduct>.IndexKeys.Ascending("sizes.size"), new CreateIndexOptions { Name = "ix_sizes_size" })
    });

    // Index cho bảng StockReservations:
    // - ux_orderId (Unique): Đảm bảo tính Lũy đẳng (Idempotency), chống khóa kho 2 lần cho cùng 1 OrderId
    // - ix_status_expiresAt: Giúp Background Worker quét các phiên hết hạn 15 phút cực nhanh
    reservationsCollection.Indexes.CreateMany(new[]
    {
        new CreateIndexModel<StockReservation>(
            Builders<StockReservation>.IndexKeys.Ascending(r => r.OrderId),
            new CreateIndexOptions { Unique = true, Name = "ux_orderId" }),
        new CreateIndexModel<StockReservation>(
            Builders<StockReservation>.IndexKeys.Ascending(r => r.Status).Ascending(r => r.ExpiresAt),
            new CreateIndexOptions { Name = "ix_status_expiresAt" })
    });

    // Tự động Seed 6 mẫu giày Adidas thật nếu database trống
    CatalogDataSeeder.SeedAsync(productsCollection).GetAwaiter().GetResult();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Lỗi khi khởi tạo index hoặc seed data cho CatalogService.");
}

// 7. HTTP Pipeline: Bật Swagger cho cả Development và local test
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CatalogService v1"));

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
