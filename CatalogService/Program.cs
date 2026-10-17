using System.Text;
using CatalogService.Data;
using CatalogService.Models;
using CatalogService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// 1. Dependency Injection
builder.Services.AddScoped<ICatalogService, ProductCatalogService>();

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
        Description = "Microservice trưng bày và tra cứu danh mục giày thể thao / Unisex Adidas."
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
    var connectionString = config["CatalogDbSettings:ConnectionString"]
                           ?? config["MongoDbSettings:ConnectionString"]
                           ?? "mongodb://localhost:27017";
    var databaseName = config["CatalogDbSettings:DatabaseName"] ?? "CatalogDb";
    var client = new MongoClient(connectionString);
    return client.GetDatabase(databaseName);
});

var app = builder.Build();

// 6. Tự động khởi tạo Index & Seed dữ liệu mẫu khi khởi động
try
{
    var db = app.Services.GetRequiredService<IMongoDatabase>();
    var productsCollection = db.GetCollection<ShoeProduct>("Products");

    // Index cho tìm kiếm và lọc nhanh
    productsCollection.Indexes.CreateMany(new[]
    {
        new CreateIndexModel<ShoeProduct>(Builders<ShoeProduct>.IndexKeys.Ascending(p => p.Category), new CreateIndexOptions { Name = "ix_category" }),
        new CreateIndexModel<ShoeProduct>(Builders<ShoeProduct>.IndexKeys.Ascending(p => p.Price), new CreateIndexOptions { Name = "ix_price" }),
        new CreateIndexModel<ShoeProduct>(Builders<ShoeProduct>.IndexKeys.Ascending("sizes.size"), new CreateIndexOptions { Name = "ix_sizes_size" })
    });

    // Tự động Seed 6 mẫu giày Adidas thật nếu database trống
    CatalogDataSeeder.SeedAsync(productsCollection).GetAwaiter().GetResult();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Lỗi khi khởi tạo index hoặc seed data cho CatalogService.");
}

// 7. HTTP Pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CatalogService v1"));
}

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
