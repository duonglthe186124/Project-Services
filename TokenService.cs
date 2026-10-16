using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuthService.Models;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;

namespace AuthService.Services;

public record IssuedToken(string Token, DateTime ExpiresAt);

public interface ITokenService
{
    /// <summary>Tạo Access Token (JWT) thời hạn ngắn.</summary>
    IssuedToken GenerateAccessToken(User user);

    /// <summary>Tạo và lưu Refresh Token mới cho user.</summary>
    Task<IssuedToken> CreateRefreshTokenAsync(string userId, string? ip, string? userAgent);

    /// <summary>
    /// Tiêu thụ (thu hồi) Refresh Token để xoay vòng. Trả về UserId nếu hợp lệ, null nếu không hợp lệ.
    /// Nếu phát hiện token đã bị thu hồi được dùng lại -> thu hồi toàn bộ token của user (chống đánh cắp token).
    /// </summary>
    Task<string?> ConsumeRefreshTokenAsync(string refreshToken);

    /// <summary>Thu hồi 1 Refresh Token (đăng xuất thiết bị hiện tại).</summary>
    Task RevokeRefreshTokenAsync(string refreshToken, string reason);

    /// <summary>Thu hồi toàn bộ Refresh Token của user (đăng xuất mọi thiết bị).</summary>
    Task RevokeAllForUserAsync(string userId, string reason);
}

public class TokenService : ITokenService
{
    public const string RefreshTokensCollectionName = "RefreshTokens";

    private readonly IConfiguration _config;
    private readonly IMongoCollection<RefreshToken> _refreshTokens;
    private readonly ILogger<TokenService> _logger;

    public TokenService(IConfiguration config, IMongoDatabase database, ILogger<TokenService> logger)
    {
        _config = config;
        _refreshTokens = database.GetCollection<RefreshToken>(RefreshTokensCollectionName);
        _logger = logger;
    }

    public IssuedToken GenerateAccessToken(User user)
    {
        var secret = _config["JwtSettings:Secret"]!;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id ?? ""),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("fullName", user.FullName ?? string.Empty)
        };

        var expiresAt = DateTime.UtcNow.AddMinutes(_config.GetValue("JwtSettings:ExpiryMinutes", 15));

        var token = new JwtSecurityToken(
            issuer: _config["JwtSettings:Issuer"],
            audience: _config["JwtSettings:Audience"],
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds
        );

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public async Task<IssuedToken> CreateRefreshTokenAsync(string userId, string? ip, string? userAgent)
    {
        // 64 byte ngẫu nhiên an toàn mật mã -> không thể đoán
        var rawToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
        var expiresAt = DateTime.UtcNow.AddDays(_config.GetValue("JwtSettings:RefreshTokenExpiryDays", 30));

        await _refreshTokens.InsertOneAsync(new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(rawToken),
            ExpiresAt = expiresAt,
            CreatedByIp = ip,
            UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent
        });

        return new IssuedToken(rawToken, expiresAt);
    }

    public async Task<string?> ConsumeRefreshTokenAsync(string refreshToken)
    {
        var hash = Hash(refreshToken);
        var now = DateTime.UtcNow;

        // Thu hồi nguyên tử: 2 request dùng cùng 1 token cùng lúc thì chỉ 1 request thành công
        var consumed = await _refreshTokens.FindOneAndUpdateAsync(
            t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now,
            Builders<RefreshToken>.Update
                .Set(t => t.RevokedAt, now)
                .Set(t => t.RevokedReason, "rotated"));

        if (consumed != null)
            return consumed.UserId;

        // Token không hợp lệ: kiểm tra có phải token ĐÃ BỊ THU HỒI đang bị dùng lại không
        var existing = await _refreshTokens.Find(t => t.TokenHash == hash).FirstOrDefaultAsync();
        if (existing?.RevokedAt != null && existing.RevokedReason == "rotated")
        {
            // Dấu hiệu token bị đánh cắp -> thu hồi toàn bộ phiên của user
            _logger.LogWarning("Phát hiện Refresh Token đã xoay vòng bị dùng lại. UserId={UserId}. Thu hồi toàn bộ phiên.", existing.UserId);
            await RevokeAllForUserAsync(existing.UserId, "reuse-detected");
        }

        return null;
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, string reason)
    {
        var hash = Hash(refreshToken);
        await _refreshTokens.UpdateOneAsync(
            t => t.TokenHash == hash && t.RevokedAt == null,
            Builders<RefreshToken>.Update
                .Set(t => t.RevokedAt, DateTime.UtcNow)
                .Set(t => t.RevokedReason, reason));
    }

    public async Task RevokeAllForUserAsync(string userId, string reason)
    {
        await _refreshTokens.UpdateManyAsync(
            t => t.UserId == userId && t.RevokedAt == null,
            Builders<RefreshToken>.Update
                .Set(t => t.RevokedAt, DateTime.UtcNow)
                .Set(t => t.RevokedReason, reason));
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}