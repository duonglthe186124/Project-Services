using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AuthService.Services;

public interface IEmailService
{
    Task SendOtpEmailAsync(string toEmail, string otp);
    Task SendResetPasswordEmailAsync(string toEmail, string otp);
}

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, IWebHostEnvironment env, ILogger<EmailService> logger)
    {
        _config = config;
        _env = env;
        _logger = logger;
    }

    public async Task SendOtpEmailAsync(string toEmail, string otp)
    {
        var subject = $"{otp} là mã xác thực tài khoản của bạn";
        var intro = "Cảm ơn bạn đã đăng ký tài khoản. Vui lòng sử dụng mã bên dưới để xác thực địa chỉ email của bạn.";
        await SendEmailAsync(toEmail, subject, "Xác thực tài khoản", intro, otp);
    }

    public async Task SendResetPasswordEmailAsync(string toEmail, string otp)
    {
        var subject = $"{otp} là mã đặt lại mật khẩu của bạn";
        var intro = "Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản của bạn. Vui lòng sử dụng mã bên dưới để tiếp tục.";
        await SendEmailAsync(toEmail, subject, "Đặt lại mật khẩu", intro, otp);
    }

    private async Task SendEmailAsync(string toEmail, string subject, string title, string intro, string otp)
    {
        // Chỉ log OTP khi chạy Development để tiện test, tránh lộ OTP trên server thật
        if (_env.IsDevelopment())
            _logger.LogInformation("[OTP LOG] To: {Email} | OTP: {Otp}", toEmail, otp);

        var smtpServer = _config["EmailSettings:SmtpServer"] ?? "smtp.gmail.com";
        var smtpPort = int.Parse(_config["EmailSettings:SmtpPort"] ?? "587");
        var senderName = _config["EmailSettings:SenderName"] ?? "AuthService";
        var senderEmail = _config["EmailSettings:SenderEmail"] ?? "";
        var appPassword = (_config["EmailSettings:AppPassword"] ?? "").Replace(" ", "");
        var expiryMinutes = _config["OtpSettings:ExpiryMinutes"] ?? "10";

        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(senderName, senderEmail));
        email.ReplyTo.Add(new MailboxAddress(senderName, senderEmail));
        email.To.Add(MailboxAddress.Parse(toEmail));
        email.Subject = subject;

        var builder = new BodyBuilder
        {
            // Gửi kèm bản plain text: bộ lọc spam đánh giá thấp email chỉ có HTML hoặc chỉ có 1 dòng text
            TextBody =
                $"{title}\n\n" +
                $"Xin chào,\n\n{intro}\n\n" +
                $"Mã xác thực: {otp}\n" +
                $"Mã có hiệu lực trong {expiryMinutes} phút.\n\n" +
                "Không chia sẻ mã này cho bất kỳ ai. Nếu bạn không thực hiện yêu cầu này, vui lòng bỏ qua email.\n\n" +
                $"— {senderName}",
            HtmlBody = BuildHtml(senderName, title, intro, otp, expiryMinutes)
        };
        email.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(smtpServer, smtpPort, SecureSocketOptions.StartTls);
        await smtp.AuthenticateAsync(senderEmail, appPassword);
        await smtp.SendAsync(email);
        await smtp.DisconnectAsync(true);
    }

    private static string BuildHtml(string senderName, string title, string intro, string otp, string expiryMinutes)
    {
        var name = WebUtility.HtmlEncode(senderName);
        return $@"<!DOCTYPE html>
<html lang=""vi"">
<head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1.0""><title>{WebUtility.HtmlEncode(title)}</title></head>
<body style=""margin:0;padding:0;background:#f4f6f8;font-family:Arial,Helvetica,sans-serif;color:#333;"">
  <table role=""presentation"" width=""100%"" cellspacing=""0"" cellpadding=""0"" style=""background:#f4f6f8;padding:24px 0;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""480"" cellspacing=""0"" cellpadding=""0"" style=""background:#ffffff;border-radius:8px;overflow:hidden;max-width:480px;"">
        <tr><td style=""background:#2563eb;color:#ffffff;padding:20px 24px;font-size:20px;font-weight:bold;"">{name}</td></tr>
        <tr><td style=""padding:24px;"">
          <h2 style=""margin:0 0 12px;font-size:18px;"">{WebUtility.HtmlEncode(title)}</h2>
          <p style=""margin:0 0 16px;line-height:1.5;"">Xin chào,</p>
          <p style=""margin:0 0 20px;line-height:1.5;"">{WebUtility.HtmlEncode(intro)}</p>
          <div style=""text-align:center;margin:24px 0;"">
            <span style=""display:inline-block;font-size:32px;letter-spacing:8px;font-weight:bold;background:#f1f5f9;border:1px dashed #94a3b8;border-radius:6px;padding:12px 24px;color:#111827;"">{otp}</span>
          </div>
          <p style=""margin:0 0 8px;line-height:1.5;"">Mã có hiệu lực trong <b>{expiryMinutes} phút</b>.</p>
          <p style=""margin:0 0 8px;line-height:1.5;color:#b91c1c;"">Không chia sẻ mã này cho bất kỳ ai, kể cả nhân viên hỗ trợ.</p>
          <p style=""margin:16px 0 0;line-height:1.5;color:#6b7280;font-size:13px;"">Nếu bạn không thực hiện yêu cầu này, vui lòng bỏ qua email. Tài khoản của bạn vẫn an toàn.</p>
        </td></tr>
        <tr><td style=""background:#f9fafb;padding:16px 24px;font-size:12px;color:#9ca3af;text-align:center;"">
          Email được gửi tự động từ {name}. Vui lòng không trả lời email này.
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
    }
}