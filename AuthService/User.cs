using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using System;

namespace AuthService.Models;
public class User
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("email")]
    public string Email { get; set; } = string.Empty;

    [BsonElement("passwordHash")]
    public string? PasswordHash { get; set; }

    [BsonElement("isEmailVerified")]
    public bool IsEmailVerified { get; set; } = false;

    // Phân quyền: "Customer" (mặc định) hoặc "Admin"
    [BsonElement("role")]
    public string Role { get; set; } = "Customer";

    // Dành cho OTP đăng ký
    [BsonElement("verificationOtp")]
    public string? VerificationOtp { get; set; }

    [BsonElement("otpExpiresAt")]
    public DateTime? OtpExpiresAt { get; set; }

    // Số lần nhập sai OTP đăng ký (chống brute-force)
    [BsonElement("otpFailedAttempts")]
    public int OtpFailedAttempts { get; set; } = 0;

    // Thời điểm gửi OTP đăng ký gần nhất (chống spam gửi mail)
    [BsonElement("otpLastSentAt")]
    public DateTime? OtpLastSentAt { get; set; }

    // Dành cho OTP quên mật khẩu
    [BsonElement("resetPasswordOtp")]
    public string? ResetPasswordOtp { get; set; }

    [BsonElement("resetPasswordOtpExpiresAt")]
    public DateTime? ResetPasswordOtpExpiresAt { get; set; }

    // Số lần nhập sai OTP đặt lại mật khẩu (chống brute-force)
    [BsonElement("resetPasswordOtpFailedAttempts")]
    public int ResetPasswordOtpFailedAttempts { get; set; } = 0;

    // Thời điểm gửi OTP đặt lại mật khẩu gần nhất (chống spam gửi mail)
    [BsonElement("resetPasswordOtpLastSentAt")]
    public DateTime? ResetPasswordOtpLastSentAt { get; set; }

    // Dành cho tài khoản Google
    [BsonElement("googleId")]
    public string? GoogleId { get; set; }

    [BsonElement("fullName")]
    public string? FullName { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
