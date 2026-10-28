using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Data;

/// <summary>
/// Khởi tạo Collection "PaymentTransactions" và các Index cần thiết cho database "PaymentDb".
/// Đảm bảo chạy nhiều lần an toàn (Idempotent).
/// </summary>
public static class PaymentDbInitializer
{
    public const string PaymentTransactionsCollectionName = "PaymentTransactions";

    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
        var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger(typeof(PaymentDbInitializer));
        await InitializeAsync(database, logger);
    }

    public static async Task InitializeAsync(IMongoDatabase database, ILogger? logger = null)
    {
        logger?.LogInformation("Bắt đầu khởi tạo MongoDB Collection & Indexes cho PaymentDb...");

        var collection = database.GetCollection<PaymentTransaction>(PaymentTransactionsCollectionName);

        var indexes = new List<CreateIndexModel<PaymentTransaction>>
        {
            // 1. Unique Index trên TransactionCode
            new(
                Builders<PaymentTransaction>.IndexKeys.Ascending(t => t.TransactionCode),
                new CreateIndexOptions
                {
                    Name = "ux_transactionCode",
                    Unique = true
                }),

            // 2. Index tra cứu theo OrderId
            new(
                Builders<PaymentTransaction>.IndexKeys
                    .Ascending(t => t.OrderId)
                    .Descending(t => t.CreatedAt),
                new CreateIndexOptions
                {
                    Name = "ix_orderId_createdAt"
                }),

            // 3. Index tra cứu theo OrderCode (dùng khi SePay Webhook bắn về chỉ có nội dung chứa mã ADI-...)
            new(
                Builders<PaymentTransaction>.IndexKeys.Ascending(t => t.OrderCode),
                new CreateIndexOptions
                {
                    Name = "ix_orderCode"
                }),

            // 4. Unique Partial Index trên SePayTransactionId để chống xử lý trùng lặp Webhook từ SePay
            new(
                Builders<PaymentTransaction>.IndexKeys.Ascending(t => t.SePayTransactionId),
                new CreateIndexOptions<PaymentTransaction>
                {
                    Name = "ux_sePayTransactionId",
                    Unique = true,
                    PartialFilterExpression = Builders<PaymentTransaction>.Filter.Gt(t => t.SePayTransactionId, 0L)
                })
        };

        await collection.Indexes.CreateManyAsync(indexes);
        logger?.LogInformation("Khởi tạo MongoDB Indexes cho PaymentDb hoàn tất thành công (4 indexes).");
    }
}
