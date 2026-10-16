using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuthService.Dtos;
using AuthService.Models;
using AuthService.Services;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace AuthService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IMongoCollection<User> _usersCollection;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    private readonly int _otpExpiryMinutes;
    private readonly int _otpResendCooldownSeconds;
    private readonly int _maxOtpAttempts;

    private const string GenericForgotMessage = "Nếu email tồn tại trong hệ thống, mã xác nhận đặt lại mật khẩu đã được gửi.";
    private const string InvalidOtpMessage = "Mã OTP không đúng hoặc đã hết hạn.";

    public AuthController(
        IMongoDatabase database,
        ITokenService tokenService,
        IEmailService emailService,
        IConfiguration config,
        ILogger<AuthController> logger)
    {
        _usersCollection = database.GetCollection<User>("Users");
        _tokenService = tokenService;
        _emailService = emailService;
        _config = config;
        _logger = logger;

        _otpExpiryMinutes = config.GetValue("OtpSettings:ExpiryMinutes", 10);
        _otpResendCooldownSeconds = config.GetValue("OtpSettings:ResendCooldownSeconds", 60);
        _maxOtpAttempts = config.GetValue("OtpSettings:MaxFailedAttempts", 5);

        // Unique Index cho Email đã được chuyển sang tạo 1 lần khi khởi động ứng dụng (Program.cs)
    }

    // 1. Đăng ký tài khoản
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var email = NormalizeEmail(req.Email);
        var existingUser = await FindByEmailAsync(email);

        if (existingUser != null && existingUser.IsEmailVerified)
            return BadRequest(new { message = "Email này đã được sử dụng." });

        var otp = GenerateOtp();
        var now = DateTime.UtcNow;
        var isNewUser = existingUser == null;
        User user;

        if (existingUser != null)
        {
            // Tài khoản đã đăng ký nhưng CHƯA xác thực -> cho phép đăng ký lại (tránh kẹt tài khoản "zombie")
            var wait = GetCooldownRemaining(existingUser.OtpLastSentAt);
            if (wait > 0)
                return TooManyRequestsResult(wait);

            var update = Builders<User>.Update
                .Set(u => u.PasswordHash, BCrypt.Net.BCrypt.HashPassword(req.Password))
                .Set(u => u.FullName, req.FullName.Trim())
                .Set(u => u.VerificationOtp, otp)
                .Set(u => u.OtpExpiresAt, now.AddMinutes(_otpExpiryMinutes))
                .Set(u => u.OtpFailedAttempts, 0)
                .Set(u => u.OtpLastSentAt, now);

            await _usersCollection.UpdateOneAsync(u => u.Id == existingUser.Id, update);
            user = existingUser;
        }
        else
        {
            user = new User
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                FullName = req.FullName.Trim(),
                IsEmailVerified = false,
                VerificationOtp = otp,
                OtpExpiresAt = now.AddMinutes(_otpExpiryMinutes),
                OtpFailedAttempts = 0,
                OtpLastSentAt = now
            };

            try
            {
                await _usersCollection.InsertOneAsync(user);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // Race condition: 2 request đăng ký cùng email cùng lúc -> Unique Index chặn lại
                return BadRequest(new { message = "Email này đã được sử dụng." });
            }
        }

        try
        {
            await _emailService.SendOtpEmailAsync(email, otp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gửi email OTP đăng ký thất bại tới {Email}", email);

            if (isNewUser)
            {
                // Rollback: xoá tài khoản vừa tạo để người dùng có thể đăng ký lại
                await _usersCollection.DeleteOneAsync(u => u.Id == user.Id);
            }
            else
            {
                // Cho phép thử lại ngay, không bắt chờ cooldown
                await _usersCollection.UpdateOneAsync(u => u.Id == user.Id,
                    Builders<User>.Update.Set(u => u.OtpLastSentAt, null));
            }

            return StatusCode(500, new { message = "Không thể gửi email xác thực. Vui lòng thử lại sau." });
        }

        return Ok(new { message = "Đăng ký thành công! Vui lòng kiểm tra email (kể cả thư mục Spam) để nhận mã OTP xác thực." });
    }

    // 2. Xác thực OTP đăng ký
    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest req)
    {
        var user = await FindByEmailAsync(NormalizeEmail(req.Email));
        if (user == null)
            return BadRequest(new { message = InvalidOtpMessage });

        if (user.IsEmailVerified)
            return BadRequest(new { message = "Tài khoản đã được xác thực trước đó." });

        if (string.IsNullOrEmpty(user.VerificationOtp) || user.OtpExpiresAt == null || user.OtpExpiresAt < DateTime.UtcNow)
            return BadRequest(new { message = "Mã OTP đã hết hạn hoặc không tồn tại. Vui lòng yêu cầu gửi lại mã mới." });

        if (user.OtpFailedAttempts >= _maxOtpAttempts)
            return BadRequest(new { message = "Bạn đã nhập sai quá nhiều lần. Vui lòng yêu cầu gửi lại mã mới." });

        if (!OtpEquals(user.VerificationOtp, req.Otp))
        {
            var attempts = user.OtpFailedAttempts + 1;
            var failUpdate = Builders<User>.Update.Inc(u => u.OtpFailedAttempts, 1);
            if (attempts >= _maxOtpAttempts)
            {
                // Vô hiệu hoá OTP khi sai quá số lần cho phép (chống brute-force)
                failUpdate = failUpdate
                    .Set(u => u.VerificationOtp, null)
                    .Set(u => u.OtpExpiresAt, null);
            }
            await _usersCollection.UpdateOneAsync(u => u.Id == user.Id, failUpdate);

            var remaining = Math.Max(0, _maxOtpAttempts - attempts);
            return BadRequest(new
            {
                message = remaining > 0
                    ? $"Mã OTP không đúng. Bạn còn {remaining} lần thử."
                    : "Bạn đã nhập sai quá nhiều lần. Vui lòng yêu cầu gửi lại mã mới.",
                remainingAttempts = remaining
            });
        }

        var update = Builders<User>.Update
            .Set(u => u.IsEmailVerified, true)
            .Set(u => u.VerificationOtp, null)
            .Set(u => u.OtpExpiresAt, null)
            .Set(u => u.OtpFailedAttempts, 0);

        await _usersCollection.UpdateOneAsync(u => u.Id == user.Id, update);

        return Ok(await IssueTokensAsync(user));
    }

    // 3. Gửi lại OTP đăng ký
    [HttpPost("resend-otp")]
    public async Task<IActionResult> ResendOtp([FromBody] ResendOtpRequest req)
    {
        const string genericMessage = "Nếu tài khoản tồn tại và chưa được xác thực, mã OTP mới đã được gửi tới email.";

        var email = NormalizeEmail(req.Email);
        var user = await FindByEmailAsync(email);
        if (user == null)
            return Ok(new { message = genericMessage });

        if (user.IsEmailVerified)
            return BadRequest(new { message = "Tài khoản đã được xác thực. Vui lòng đăng nhập." });

        var wait = GetCooldownRemaining(user.OtpLastSentAt);
        if (wait > 0)
            return TooManyRequestsResult(wait);

        var otp = GenerateOtp();
        var now = DateTime.UtcNow;
        var update = Builders<User>.Update
            .Set(u => u.VerificationOtp, otp)
            .Set(u => u.OtpExpiresAt, now.AddMinutes(_otpExpiryMinutes))
            .Set(u => u.OtpFailedAttempts, 0)
            .Set(u => u.OtpLastSentAt, now);

        await _usersCollection.UpdateOneAsync(u => u.Id == user.Id, update);

        try
        {
            await _emailService.SendOtpEmailAsync(email, otp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gửi lại email OTP thất bại tới {Email}", email);
            await _usersCollection.UpdateOneAsync(u => u.Id == user.Id,
                Builders<User>.Update.Set(u => u.OtpLastSentAt, null));
            return StatusCode(500, new { message = "Không thể gửi email. Vui lòng thử lại sau." });
        }

        return Ok(new { message = genericMessage });
    }

    // 4. Đăng nhập truyền thống
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await FindByEmailAsync(NormalizeEmail(req.Email));
        if (user == null || string.IsNullOrEmpty(user.PasswordHash))
            return Unauthorized(new { message = "Email hoặc mật khẩu không chính xác." });

        if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { message = "Email hoặc mật khẩu không chính xác." });

        if (!user.IsEmailVerified)
            return BadRequest(new { message = "Vui lòng xác thực email trước khi đăng nhập.", requiresVerification = true });

        return Ok(await IssueTokensAsync(user));
    }

    // 5. Quên mật khẩu - Gửi OTP đặt lại
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        var email = NormalizeEmail(req.Email);
        var user = await FindByEmailAsync(email);
        if (user == null)
        {
            // Trả về OK để tránh bị dò quét danh sách email tồn tại trong hệ thống
            return Ok(new { message = GenericForgotMessage });
        }

        var wait = GetCooldownRemaining(user.ResetPasswordOtpLastSentAt);
        if (wait > 0)
            return TooManyRequestsResult(wait);

        var otp = GenerateOtp();
        var now = DateTime.UtcNow;
        var update = Builders<User>.Update
            .Set(u => u.ResetPasswordOtp, otp)
            .Set(u => u.ResetPasswordOtpExpiresAt, now.AddMinutes(_otpExpiryMinutes))
            .Set(u => u.ResetPasswordOtpFailedAttempts, 0)
            .Set(u => u.ResetPasswordOtpLastSentAt, now);

        await _usersCollection.UpdateOneAsync(u => u.Id == user.Id, update);

        try
        {
            await _emailService.SendResetPasswordEmailAsync(email, otp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gửi email OTP đặt lại mật khẩu thất bại tới {Email}", email);
            await _usersCollection.UpdateOneAsync(u => u.Id == user.Id,
                Builders<User>.Update.Set(u => u.ResetPasswordOtpLastSentAt, null));
            return StatusCode(500, new { message = "Không thể gửi email. Vui lòng thử lại sau." });
        }

        return Ok(new { message = GenericForgotMessage });
    }

    // 6. Đặt lại mật khẩu mới bằng OTP
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    {
        var user = await FindByEmailAsync(NormalizeEmail(req.Email));
        if (user == null)
            return BadRequest(new { message = InvalidOtpMessage });

        if (string.IsNullOrEmpty(user.ResetPasswordOtp) ||
            user.ResetPasswordOtpExpiresAt == null ||
            user.ResetPasswordOtpExpiresAt < DateTime.UtcNow)
        {
            return BadRequest(new { message = "Mã OTP đã hết hạn hoặc không tồn tại. Vui lòng yêu cầu mã mới." });
        }

        if (user.ResetPasswordOtpFailedAttempts >= _maxOtpAttempts)
            return BadRequest(new { message = "Bạn đã nhập sai quá nhiều lần. Vui lòng yêu cầu mã mới." });

        if (!OtpEquals(user.ResetPasswordOtp, req.Otp))
        {
            var attempts = user.ResetPasswordOtpFailedAttempts + 1;
            var failUpdate = Builders<User>.Update.Inc(u => u.ResetPasswordOtpFailedAttempts, 1);
            if (attempts >= _maxOtpAttempts)
            {
                failUpdate = failUpdate
                    .Set(u => u.ResetPasswordOtp, null)
                    .Set(u => u.ResetPasswordOtpExpiresAt, null);
            }
            await _usersCollection.UpdateOneAsync(u => u.Id == user.Id, failUpdate);

            var remaining = Math.Max(0, _maxOtpAttempts - attempts);
            return BadRequest(new
            {
                message = remaining > 0
                    ? $"Mã OTP không đúng. Bạn còn {remaining} lần thử."
                    : "Bạn đã nhập sai quá nhiều lần. Vui lòng yêu cầu mã mới.",
                remainingAttempts = remaining
            });
        }

        var newPasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        var update = Builders<User>.Update
            .Set(u => u.PasswordHash, newPasswordHash)
            .Set(u => u.ResetPasswordOtp, null)
            .Set(u => u.ResetPasswordOtpExpiresAt, null)
            .Set(u => u.ResetPasswordOtpFailedAttempts, 0)
            .Set(u => u.IsEmailVerified, true) // Thiết lập mật khẩu thành công cũng đồng nghĩa xác thực email
            .Set(u => u.VerificationOtp, null)
            .Set(u => u.OtpExpiresAt, null);

        await _usersCollection.UpdateOneAsync(u => u.Id == user.Id, update);

        // Mật khẩu đã đổi -> đăng xuất mọi thiết bị đang đăng nhập
        await _tokenService.RevokeAllForUserAsync(user.Id!, "password-reset");

        return Ok(new { message = "Đặt lại mật khẩu thành công! Bạn có thể đăng nhập bằng mật khẩu mới." });
    }

    // 7. Đăng nhập / Đăng ký qua Google ID Token
    [HttpPost("google")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest req)
    {
        try
        {
            var clientId = _config["GoogleAuth:ClientId"];
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { clientId }
            };

            var payload = await GoogleJsonWebSignature.ValidateAsync(req.IdToken, settings);
            if (!payload.EmailVerified)
                return Unauthorized(new { message = "Email Google chưa được xác minh." });

            var email = NormalizeEmail(payload.Email);
            var user = await FindByEmailAsync(email);

            if (user == null)
            {
                user = new User
                {
                    Email = email,
                    FullName = payload.Name,
                    GoogleId = payload.Subject,
                    IsEmailVerified = true
                };

                try
                {
                    await _usersCollection.InsertOneAsync(user);
                }
                catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    user = await FindByEmailAsync(email);
                    if (user == null) throw;
                }
            }
            else if (string.IsNullOrEmpty(user.GoogleId))
            {
                var update = Builders<User>.Update
                    .Set(u => u.GoogleId, payload.Subject)
                    .Set(u => u.IsEmailVerified, true)
                    .Set(u => u.VerificationOtp, null)
                    .Set(u => u.OtpExpiresAt, null);

                await _usersCollection.UpdateOneAsync(u => u.Id == user.Id, update);
            }

            return Ok(await IssueTokensAsync(user));
        }
        catch (InvalidJwtException)
        {
            return Unauthorized(new { message = "Google Token không hợp lệ hoặc đã hết hạn." });
        }
    }

    // 8. Lấy thông tin người dùng hiện tại (yêu cầu JWT)
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Token không hợp lệ." });

        var user = await _usersCollection.Find(u => u.Id == userId).FirstOrDefaultAsync();
        if (user == null)
            return NotFound(new { message = "Không tìm thấy tài khoản." });

        return Ok(new UserProfileResponse(
            user.Id!,
            user.Email,
            user.FullName ?? "",
            user.IsEmailVerified,
            !string.IsNullOrEmpty(user.PasswordHash),
            !string.IsNullOrEmpty(user.GoogleId),
            user.CreatedAt));
    }

    // 9. Làm mới Access Token bằng Refresh Token (xoay vòng: refresh token cũ bị thu hồi, cấp cặp mới)
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest req)
    {
        var userId = await _tokenService.ConsumeRefreshTokenAsync(req.RefreshToken.Trim());
        if (userId == null)
            return Unauthorized(new { message = "Refresh Token không hợp lệ hoặc đã hết hạn. Vui lòng đăng nhập lại." });

        var user = await _usersCollection.Find(u => u.Id == userId).FirstOrDefaultAsync();
        if (user == null || !user.IsEmailVerified)
        {
            await _tokenService.RevokeAllForUserAsync(userId, "user-invalid");
            return Unauthorized(new { message = "Tài khoản không hợp lệ. Vui lòng đăng nhập lại." });
        }

        return Ok(await IssueTokensAsync(user));
    }

    // 10. Đăng xuất thiết bị hiện tại (thu hồi Refresh Token được gửi lên)
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest req)
    {
        await _tokenService.RevokeRefreshTokenAsync(req.RefreshToken.Trim(), "logout");
        return Ok(new { message = "Đăng xuất thành công." });
    }

    // 11. Đăng xuất khỏi tất cả thiết bị
    [Authorize]
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Token không hợp lệ." });

        await _tokenService.RevokeAllForUserAsync(userId, "logout-all");
        return Ok(new { message = "Đã đăng xuất khỏi tất cả thiết bị." });
    }

    // 12. Đổi mật khẩu khi đã đăng nhập
    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Token không hợp lệ." });

        var user = await _usersCollection.Find(u => u.Id == userId).FirstOrDefaultAsync();
        if (user == null)
            return NotFound(new { message = "Không tìm thấy tài khoản." });

        if (string.IsNullOrEmpty(user.PasswordHash))
            return BadRequest(new { message = "Tài khoản đăng nhập bằng Google chưa có mật khẩu. Vui lòng dùng chức năng Quên mật khẩu để thiết lập mật khẩu." });

        if (!BCrypt.Net.BCrypt.Verify(req.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "Mật khẩu hiện tại không đúng." });

        if (req.CurrentPassword == req.NewPassword)
            return BadRequest(new { message = "Mật khẩu mới phải khác mật khẩu hiện tại." });

        await _usersCollection.UpdateOneAsync(u => u.Id == user.Id,
            Builders<User>.Update.Set(u => u.PasswordHash, BCrypt.Net.BCrypt.HashPassword(req.NewPassword)));

        // Đăng xuất mọi thiết bị khác, cấp phiên mới cho thiết bị hiện tại
        await _tokenService.RevokeAllForUserAsync(user.Id!, "password-changed");

        var tokens = await IssueTokensAsync(user);
        return Ok(new { message = "Đổi mật khẩu thành công. Các thiết bị khác đã bị đăng xuất.", tokens });
    }

    // ===================== Helpers =====================

    private async Task<AuthResponse> IssueTokensAsync(User user)
    {
        var access = _tokenService.GenerateAccessToken(user);
        var refresh = await _tokenService.CreateRefreshTokenAsync(
            user.Id!,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString());

        return new AuthResponse(
            access.Token, access.ExpiresAt,
            refresh.Token, refresh.ExpiresAt,
            user.Email, user.FullName ?? "", user.Id!);
    }

    private string? GetCurrentUserId()
    {
        var principal = HttpContext.User;
        return principal.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private Task<User?> FindByEmailAsync(string normalizedEmail) =>
        _usersCollection.Find(u => u.Email == normalizedEmail).FirstOrDefaultAsync()!;

    // Dùng bộ sinh số ngẫu nhiên an toàn mật mã thay cho System.Random
    private static string GenerateOtp() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    // So sánh thời gian hằng số để tránh timing attack
    private static bool OtpEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual.Trim()));

    private int GetCooldownRemaining(DateTime? lastSentAt)
    {
        if (lastSentAt == null) return 0;
        var elapsed = (DateTime.UtcNow - lastSentAt.Value).TotalSeconds;
        var remaining = _otpResendCooldownSeconds - elapsed;
        return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
    }

    private IActionResult TooManyRequestsResult(int waitSeconds)
    {
        Response.Headers["Retry-After"] = waitSeconds.ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            message = $"Bạn vừa yêu cầu gửi mã. Vui lòng thử lại sau {waitSeconds} giây.",
            retryAfterSeconds = waitSeconds
        });
    }
}