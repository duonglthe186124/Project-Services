using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OrderService.Models;

namespace OrderService.Data;

/// <summary>
/// Khởi tạo và thiết lập các Collection và Index bắt buộc cho MongoDB database "OrderDb".
/// Đảm bảo tính Idempotent: chạy nhiều lần an toàn, không làm trùng lặp hay xung đột index.
/// </summary>
public static class OrderDbInitializer
{
    public const string CartsCollectionName = "Carts";
    public const string OrdersCollectionName = "Orders";
    public const string OrderSagaStatesCollectionName = "OrderSagaStates";
    public const string OrderCompensationsCollectionName = "OrderCompensations";
    public const string IdempotencyRecordsCollectionName = "IdempotencyRecords";

    /// <summary>
    /// Nạp dịch vụ từ IServiceProvider và gọi InitializeAsync.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
        var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger(typeof(OrderDbInitializer));
        await InitializeAsync(database, logger);
    }

    /// <summary>
    /// Tạo toàn bộ 9 chỉ mục theo đúng đặc tả kiến trúc Milestone 1.
    /// </summary>
    public static async Task InitializeAsync(IMongoDatabase database, ILogger? logger = null)
    {
        logger?.LogInformation("Bắt đầu khởi tạo MongoDB Collections & Indexes cho OrderDb...");

        // 1. Collection Carts
        var cartsCollection = database.GetCollection<Cart>(CartsCollectionName);
        await CreateCartsIndexesAsync(cartsCollection, logger);

        // 2. Collection Orders
        var ordersCollection = database.GetCollection<Order>(OrdersCollectionName);
        await CreateOrdersIndexesAsync(ordersCollection, logger);

        // 3. Collection OrderSagaStates
        var sagaStatesCollection = database.GetCollection<OrderSagaState>(OrderSagaStatesCollectionName);
        await CreateOrderSagaStatesIndexesAsync(sagaStatesCollection, logger);

        // 4. Collection OrderCompensations
        var compensationsCollection = database.GetCollection<OrderCompensation>(OrderCompensationsCollectionName);
        await CreateOrderCompensationsIndexesAsync(compensationsCollection, logger);

        // 5. Collection IdempotencyRecords
        var idempotencyCollection = database.GetCollection<IdempotencyRecord>(IdempotencyRecordsCollectionName);
        await CreateIdempotencyRecordsIndexesAsync(idempotencyCollection, logger);

        logger?.LogInformation("Khởi tạo MongoDB Collections & Indexes cho OrderDb hoàn tất thành công.");
    }

    private static async Task CreateCartsIndexesAsync(IMongoCollection<Cart> collection, ILogger? logger)
    {
        try
        {
            // Unique Index ux_userId: Mỗi người dùng chỉ có tối đa 1 giỏ hàng
            var indexModel = new CreateIndexModel<Cart>(
                Builders<Cart>.IndexKeys.Ascending(c => c.UserId),
                new CreateIndexOptions
                {
                    Name = "ux_userId",
                    Unique = true
                });

            await collection.Indexes.CreateOneAsync(indexModel);
            logger?.LogInformation("Đã khởi tạo index: Carts -> ux_userId (Unique)");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Lỗi khi tạo index ux_userId trên Carts.");
            throw;
        }
    }

    private static async Task CreateOrdersIndexesAsync(IMongoCollection<Order> collection, ILogger? logger)
    {
        try
        {
            var indexes = new List<CreateIndexModel<Order>>
            {
                // Unique Index ux_orderCode: Mã đơn hàng là duy nhất toàn hệ thống
                new(
                    Builders<Order>.IndexKeys.Ascending(o => o.OrderCode),
                    new CreateIndexOptions
                    {
                        Name = "ux_orderCode",
                        Unique = true
                    }),

                // Compound Index ix_userId_createdAt: Tra cứu danh sách đơn của khách theo thứ tự thời gian mới nhất
                new(
                    Builders<Order>.IndexKeys
                        .Ascending(o => o.UserId)
                        .Descending(o => o.CreatedAt),
                    new CreateIndexOptions
                    {
                        Name = "ix_userId_createdAt"
                    }),

                // Index ix_orderStatus: Lọc và thống kê đơn hàng theo trạng thái phục vụ Admin & LogisticsStaff
                new(
                    Builders<Order>.IndexKeys.Ascending(o => o.OrderStatus),
                    new CreateIndexOptions
                    {
                        Name = "ix_orderStatus"
                    })
            };

            await collection.Indexes.CreateManyAsync(indexes);
            logger?.LogInformation("Đã khởi tạo 3 indexes trên Orders: ux_orderCode, ix_userId_createdAt, ix_orderStatus");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Lỗi khi tạo indexes trên Orders.");
            throw;
        }
    }

    private static async Task CreateOrderSagaStatesIndexesAsync(IMongoCollection<OrderSagaState> collection, ILogger? logger)
    {
        try
        {
            var indexes = new List<CreateIndexModel<OrderSagaState>>
            {
                // Unique Index ux_orderId: Đảm bảo 1 đơn hàng chỉ có duy nhất 1 phiên điều phối Saga
                new(
                    Builders<OrderSagaState>.IndexKeys.Ascending(s => s.OrderId),
                    new CreateIndexOptions
                    {
                        Name = "ux_orderId",
                        Unique = true
                    }),

                // Compound Index ix_currentState_expiresAt: OrderSagaTimeoutWorker quét đơn hết hạn 15p cực nhanh
                new(
                    Builders<OrderSagaState>.IndexKeys
                        .Ascending(s => s.CurrentState)
                        .Ascending(s => s.ExpiresAt),
                    new CreateIndexOptions
                    {
                        Name = "ix_currentState_expiresAt"
                    })
            };

            await collection.Indexes.CreateManyAsync(indexes);
            logger?.LogInformation("Đã khởi tạo 2 indexes trên OrderSagaStates: ux_orderId, ix_currentState_expiresAt");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Lỗi khi tạo indexes trên OrderSagaStates.");
            throw;
        }
    }

    private static async Task CreateOrderCompensationsIndexesAsync(IMongoCollection<OrderCompensation> collection, ILogger? logger)
    {
        try
        {
            // Index ix_status_nextRetryAt: OrderCompensationWorker quét các bản ghi cần retry theo thứ tự thời gian
            var indexModel = new CreateIndexModel<OrderCompensation>(
                Builders<OrderCompensation>.IndexKeys
                    .Ascending(c => c.Status)
                    .Ascending(c => c.NextRetryAt),
                new CreateIndexOptions
                {
                    Name = "ix_status_nextRetryAt"
                });

            await collection.Indexes.CreateOneAsync(indexModel);
            logger?.LogInformation("Đã khởi tạo index: OrderCompensations -> ix_status_nextRetryAt");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Lỗi khi tạo index ix_status_nextRetryAt trên OrderCompensations.");
            throw;
        }
    }

    private static async Task CreateIdempotencyRecordsIndexesAsync(IMongoCollection<IdempotencyRecord> collection, ILogger? logger)
    {
        try
        {
            var indexes = new List<CreateIndexModel<IdempotencyRecord>>
            {
                // Unique Index ux_key: Chặn triệt để 2 request cùng X-Idempotency-Key xử lý song song
                new(
                    Builders<IdempotencyRecord>.IndexKeys.Ascending(r => r.Key),
                    new CreateIndexOptions
                    {
                        Name = "ux_key",
                        Unique = true
                    }),

                // TTL Index ttl_expiresAt: Tự động dọn dẹp các bản ghi quá hạn 24 giờ
                new(
                    Builders<IdempotencyRecord>.IndexKeys.Ascending(r => r.ExpiresAt),
                    new CreateIndexOptions
                    {
                        Name = "ttl_expiresAt",
                        ExpireAfter = TimeSpan.Zero
                    })
            };

            await collection.Indexes.CreateManyAsync(indexes);
            logger?.LogInformation("Đã khởi tạo 2 indexes trên IdempotencyRecords: ux_key (Unique), ttl_expiresAt (TTL)");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Lỗi khi tạo indexes trên IdempotencyRecords.");
            throw;
        }
    }
}
