
using System.Text;
using AuthService.Models;
using AuthService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;

namespace AuthService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddScoped<IEmailService, EmailService>();
            builder.Services.AddScoped<ITokenService, TokenService>();

            // CORS:
            // - Development: cho phép mọi origin để dev Flutter Web/React ở port bất kỳ không bị chặn.
            // - Môi trường khác: chỉ cho phép origin trong Cors:AllowedOrigins.
            //   (Khi có API Gateway, CORS sẽ được chuyển ra Gateway quản lý tập trung.)
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                                 ?? Array.Empty<string>();
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowFlutterApp", policy =>
                {
                    if (builder.Environment.IsDevelopment())
                        policy.SetIsOriginAllowed(_ => true);
                    else
                        policy.WithOrigins(allowedOrigins);

                    policy.AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
            });

            // JWT Authentication (bắt buộc để dùng [Authorize])
            var jwtSecret = builder.Configuration["JwtSettings:Secret"]
                            ?? throw new InvalidOperationException("Thiếu cấu hình JwtSettings:Secret");
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = builder.Configuration["JwtSettings:Issuer"],
                        ValidateAudience = true,
                        ValidAudience = builder.Configuration["JwtSettings:Audience"],
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(1)
                    };
                });
            builder.Services.AddAuthorization();

            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                // Nút "Authorize" trên Swagger để test các API cần JWT (vd: GET /api/auth/me)
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "Dán JWT token (không cần gõ chữ 'Bearer ')"
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

            builder.Services.AddSingleton<IMongoDatabase>(
                sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var connectionString = config["MongoDbSettings:ConnectionString"];
                    var databaseName = config["MongoDbSettings:DatabaseName"];
                    var client = new MongoClient(connectionString);
                    return client.GetDatabase(databaseName);
                });
            var app = builder.Build();

            // Tạo Unique Index cho Email 1 lần khi khởi động (chống trùng lặp do race condition)
            try
            {
                var users = app.Services.GetRequiredService<IMongoDatabase>().GetCollection<User>("Users");
                var indexKeys = Builders<User>.IndexKeys.Ascending(u => u.Email);
                users.Indexes.CreateOne(new CreateIndexModel<User>(indexKeys,
                    new CreateIndexOptions { Unique = true, Name = "ux_email" }));
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex,
                    "Không tạo được Unique Index cho email. Hãy kiểm tra dữ liệu trùng email trong collection Users.");
            }

            // Index cho RefreshTokens: tra cứu theo hash, thu hồi theo user, TTL tự xoá token đã hết hạn
            try
            {
                var refreshTokens = app.Services.GetRequiredService<IMongoDatabase>()
                    .GetCollection<RefreshToken>(TokenService.RefreshTokensCollectionName);
                refreshTokens.Indexes.CreateMany(new[]
                {
                    new CreateIndexModel<RefreshToken>(
                        Builders<RefreshToken>.IndexKeys.Ascending(t => t.TokenHash),
                        new CreateIndexOptions { Unique = true, Name = "ux_tokenHash" }),
                    new CreateIndexModel<RefreshToken>(
                        Builders<RefreshToken>.IndexKeys.Ascending(t => t.UserId),
                        new CreateIndexOptions { Name = "ix_userId" }),
                    new CreateIndexModel<RefreshToken>(
                        Builders<RefreshToken>.IndexKeys.Ascending(t => t.ExpiresAt),
                        new CreateIndexOptions { Name = "ttl_expiresAt", ExpireAfter = TimeSpan.Zero })
                });
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Không tạo được index cho collection RefreshTokens.");
            }

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseCors("AllowFlutterApp");

            app.UseAuthentication();
            app.UseAuthorization();


            app.MapControllers();

            app.Run();
        }
    }
}
