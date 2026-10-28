using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace PaymentService.Services;

public interface IVietQrService
{
    string BankId { get; }
    string AccountNo { get; }
    string AccountName { get; }
    string Template { get; }

    /// <summary>
    /// Sinh đường dẫn ảnh QR chuẩn Napas247 qua VietQR.io.
    /// </summary>
    string GenerateQrImageUrl(decimal amount, string orderCode);

    /// <summary>
    /// Trích xuất và chuẩn hóa mã đơn hàng Adidas (ADI-yyyyMMdd-XXXXXX) từ nội dung chuyển khoản ngân hàng.
    /// Hỗ trợ cả trường hợp ngân hàng tự động xóa dấu gạch ngang '-' hoặc thay bằng khoảng trắng (VD: ADI20261027ENGAW2 -> ADI-20261027-ENGAW2).
    /// </summary>
    string? ExtractNormalizedOrderCode(string? content);
}

public partial class VietQrService : IVietQrService
{
    // Bắt cả 3 biến thể: "ADI-20261027-ENGAW2", "ADI20261027ENGAW2", "ADI 20261027 ENGAW2"
    [GeneratedRegex(@"ADI[\s\-]*(\d{8})[\s\-]*([A-Za-z0-9]{6})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdiOrderCodeRegex();

    private readonly IConfiguration _config;

    public VietQrService(IConfiguration config)
    {
        _config = config;
    }

    public string BankId => _config["VietQrSettings:BankId"]?.Trim()
        ?? throw new InvalidOperationException("Chưa cấu hình VietQrSettings__BankId trong file môi trường (.env).");

    public string AccountNo => _config["VietQrSettings:AccountNo"]?.Trim()
        ?? throw new InvalidOperationException("Chưa cấu hình VietQrSettings__AccountNo trong file môi trường (.env).");

    public string AccountName => _config["VietQrSettings:AccountName"]?.Trim()
        ?? throw new InvalidOperationException("Chưa cấu hình VietQrSettings__AccountName trong file môi trường (.env).");

    public string Template => _config["VietQrSettings:Template"]?.Trim() is { Length: > 0 } t ? t : "compact2";

    public string GenerateQrImageUrl(decimal amount, string orderCode)
    {
        if (string.IsNullOrWhiteSpace(BankId) || string.IsNullOrWhiteSpace(AccountNo) || string.IsNullOrWhiteSpace(AccountName))
        {
            throw new InvalidOperationException("Thiếu cấu hình VietQrSettings (BankId, AccountNo, AccountName) trong file môi trường (.env).");
        }

        var roundedAmount = Math.Round(amount, 0, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);

        var safeOrderCode = Uri.EscapeDataString(orderCode.Trim());
        var safeAccountName = Uri.EscapeDataString(AccountName);

        return $"https://img.vietqr.io/image/{BankId}-{AccountNo}-{Template}.png?amount={roundedAmount}&addInfo={safeOrderCode}&accountName={safeAccountName}";
    }

    public string? ExtractNormalizedOrderCode(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var match = AdiOrderCodeRegex().Match(content);
        if (!match.Success)
        {
            return null;
        }

        var datePart = match.Groups[1].Value;
        var codePart = match.Groups[2].Value.ToUpperInvariant();
        return $"ADI-{datePart}-{codePart}";
    }
}
