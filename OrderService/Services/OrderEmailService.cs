using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using OrderService.Models;

namespace OrderService.Services;

/// <summary>
/// Gửi email xác nhận đơn bằng MailKit (cùng cấu hình EmailSettings như AuthService).
/// Thiếu cấu hình / không có email người nhận / SMTP lỗi → ghi log (kèm nội dung trên Console ở Development) và trả false.
/// </summary>
public class OrderEmailService : IOrderEmailService
{
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly ILogger<OrderEmailService> _logger;

    public OrderEmailService(IConfiguration config, IHostEnvironment env, ILogger<OrderEmailService> logger)
    {
        _config = config;
        _env = env;
        _logger = logger;
    }

    public async Task<bool> SendOrderConfirmationAsync(Order order, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(order.CustomerEmail))
            {
                _logger.LogWarning("Đơn {OrderCode} không có CustomerEmail, bỏ qua gửi email xác nhận.", order.OrderCode);
                return false;
            }

            var subject = OrderEmailBuilder.BuildSubject(order);
            var text = OrderEmailBuilder.BuildPlainText(order);
            var html = OrderEmailBuilder.BuildHtml(order);

            var smtpServer = _config["EmailSettings:SmtpServer"];
            var senderEmail = _config["EmailSettings:SenderEmail"];
            var appPassword = _config["EmailSettings:AppPassword"]?.Replace(" ", string.Empty);

            if (string.IsNullOrWhiteSpace(smtpServer) || string.IsNullOrWhiteSpace(senderEmail) || string.IsNullOrWhiteSpace(appPassword))
            {
                _logger.LogWarning("Chưa cấu hình EmailSettings (SmtpServer/SenderEmail/AppPassword). Không gửi email thật cho đơn {OrderCode}.", order.OrderCode);
                if (_env.IsDevelopment())
                {
                    Console.WriteLine($"[DEV-EMAIL] To: {order.CustomerEmail}\nSubject: {subject}\n{text}");
                }
                return false;
            }

            var port = int.TryParse(_config["EmailSettings:SmtpPort"], out var p) ? p : 587;
            var senderName = _config["EmailSettings:SenderName"] ?? "Adidas Store";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(senderName, senderEmail));
            message.To.Add(MailboxAddress.Parse(order.CustomerEmail.Trim()));
            message.Subject = subject;
            message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();

            using var smtp = new SmtpClient { Timeout = 15000 };
            await smtp.ConnectAsync(smtpServer, port, SecureSocketOptions.StartTls, ct);
            await smtp.AuthenticateAsync(senderEmail, appPassword, ct);
            await smtp.SendAsync(message, ct);
            await smtp.DisconnectAsync(true, ct);

            _logger.LogInformation("Đã gửi email xác nhận đơn {OrderCode} (mã vận đơn {Waybill}).", order.OrderCode, order.WaybillCode);
            return true;
        }
        catch (Exception ex)
        {
            // Không bao giờ làm hỏng luồng thanh toán vì email
            _logger.LogError(ex, "Gửi email xác nhận đơn {OrderCode} thất bại (bỏ qua, đơn vẫn hợp lệ).", order.OrderCode);
            return false;
        }
    }
}
