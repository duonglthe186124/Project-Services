using System;
using System.ComponentModel.DataAnnotations;

namespace AuthService.Dtos;

public record RegisterRequest(
    [Required(ErrorMessage = "Email là bắt buộc.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    [MaxLength(254)]
    string Email,

    [Required(ErrorMessage = "Mật khẩu là bắt buộc.")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu phải từ 6 đến 100 ký tự.")]
    string Password,

    [Required(ErrorMessage = "Họ tên là bắt buộc.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Họ tên tối đa 100 ký tự.")]
    string FullName);

public record VerifyOtpRequest(
    [Required(ErrorMessage = "Email là bắt buộc.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    string Email,

    [Required(ErrorMessage = "OTP là bắt buộc.")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "OTP phải gồm đúng 6 chữ số.")]
    string Otp);

public record ResendOtpRequest(
    [Required(ErrorMessage = "Email là bắt buộc.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    string Email);

public record LoginRequest(
    [Required(ErrorMessage = "Email là bắt buộc.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    string Email,

    [Required(ErrorMessage = "Mật khẩu là bắt buộc.")]
    string Password);

public record GoogleLoginRequest(
    [Required(ErrorMessage = "IdToken là bắt buộc.")]
    string IdToken);

public record ForgotPasswordRequest(
    [Required(ErrorMessage = "Email là bắt buộc.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    string Email);

public record ResetPasswordRequest(
    [Required(ErrorMessage = "Email là bắt buộc.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    string Email,

    [Required(ErrorMessage = "OTP là bắt buộc.")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "OTP phải gồm đúng 6 chữ số.")]
    string Otp,

    [Required(ErrorMessage = "Mật khẩu mới là bắt buộc.")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu phải từ 6 đến 100 ký tự.")]
    string NewPassword);

public record RefreshTokenRequest(
    [Required(ErrorMessage = "RefreshToken là bắt buộc.")]
    string RefreshToken);

public record LogoutRequest(
    [Required(ErrorMessage = "RefreshToken là bắt buộc.")]
    string RefreshToken);

public record ChangePasswordRequest(
    [Required(ErrorMessage = "Mật khẩu hiện tại là bắt buộc.")]
    string CurrentPassword,

    [Required(ErrorMessage = "Mật khẩu mới là bắt buộc.")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu phải từ 6 đến 100 ký tự.")]
    string NewPassword);

public record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    string Email,
    string FullName,
    string UserId);

public record UserProfileResponse(string UserId, string Email, string FullName, bool IsEmailVerified, bool HasPassword, bool IsGoogleLinked, DateTime CreatedAt);
