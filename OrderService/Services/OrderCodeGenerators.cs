using System;
using System.Security.Cryptography;

namespace OrderService.Services;

/// <summary>
/// Sinh mã đơn hàng công khai: ADI-yyyyMMdd-XXXXXX (6 ký tự A-Z/2-9 bỏ ký tự dễ nhầm I, O, 0, 1).
/// Dùng CSPRNG để mã không đoán được; Unique Index ux_orderCode là lưới an toàn cuối cùng.
/// </summary>
public static class OrderCodeGenerator
{
    public const string Prefix = "ADI";
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate(DateTime? utcNow = null) =>
        $"{Prefix}-{(utcNow ?? DateTime.UtcNow):yyyyMMdd}-{RandomSuffix(6)}";

    internal static string RandomSuffix(int length)
    {
        Span<char> buffer = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(buffer);
    }
}

/// <summary>
/// Sinh mã vận đơn nội bộ: WB-yyyyMMdd-XXXXXX (được sinh khi đơn chuyển sang ReadyForDispatch).
/// </summary>
public static class WaybillCodeGenerator
{
    public const string Prefix = "WB";

    public static string Generate(DateTime? utcNow = null) =>
        $"{Prefix}-{(utcNow ?? DateTime.UtcNow):yyyyMMdd}-{OrderCodeGenerator.RandomSuffix(6)}";
}
