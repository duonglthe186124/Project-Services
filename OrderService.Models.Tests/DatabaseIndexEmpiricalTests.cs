using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using OrderService.Data;
using OrderService.Models;
using Xunit;

namespace OrderService.Models.Tests;

public class DatabaseIndexEmpiricalTests
{
    private static readonly string ConnectionString = TestMongoConfig.GetConnectionString();
    private readonly IMongoDatabase _database;

    public DatabaseIndexEmpiricalTests()
    {
        var client = new MongoClient(ConnectionString);
        // Use an isolated test database to avoid disrupting existing collections
        _database = client.GetDatabase("OrderDb_Test_Empirical");
    }

    [Fact]
    public async Task InitializeAsync_Creates_All_9_Indexes_And_Is_Idempotent_On_Repeated_Run()
    {
        // 1. First execution
        await OrderDbInitializer.InitializeAsync(_database);

        // 2. Second execution (must not throw; must be strictly idempotent)
        var exception = await Record.ExceptionAsync(() => OrderDbInitializer.InitializeAsync(_database));
        Assert.Null(exception);

        // 3. Verify Carts Indexes
        var cartsCollection = _database.GetCollection<Cart>(OrderDbInitializer.CartsCollectionName);
        var cartsIndexes = (await cartsCollection.Indexes.ListAsync()).ToList();
        var uxUserId = cartsIndexes.FirstOrDefault(idx => idx["name"] == "ux_userId");
        Assert.NotNull(uxUserId);
        Assert.True(uxUserId.GetValue("unique", false).AsBoolean);
        Assert.Equal(1, uxUserId["key"]["userId"].AsInt32);

        // 4. Verify Orders Indexes
        var ordersCollection = _database.GetCollection<Order>(OrderDbInitializer.OrdersCollectionName);
        var ordersIndexes = (await ordersCollection.Indexes.ListAsync()).ToList();

        var uxOrderCode = ordersIndexes.FirstOrDefault(idx => idx["name"] == "ux_orderCode");
        Assert.NotNull(uxOrderCode);
        Assert.True(uxOrderCode.GetValue("unique", false).AsBoolean);
        Assert.Equal(1, uxOrderCode["key"]["orderCode"].AsInt32);

        var ixUserIdCreatedAt = ordersIndexes.FirstOrDefault(idx => idx["name"] == "ix_userId_createdAt");
        Assert.NotNull(ixUserIdCreatedAt);
        Assert.Equal(1, ixUserIdCreatedAt["key"]["userId"].AsInt32);
        Assert.Equal(-1, ixUserIdCreatedAt["key"]["createdAt"].AsInt32); // Descending!

        var ixOrderStatus = ordersIndexes.FirstOrDefault(idx => idx["name"] == "ix_orderStatus");
        Assert.NotNull(ixOrderStatus);
        Assert.Equal(1, ixOrderStatus["key"]["orderStatus"].AsInt32);

        // 5. Verify OrderSagaStates Indexes
        var sagaCollection = _database.GetCollection<OrderSagaState>(OrderDbInitializer.OrderSagaStatesCollectionName);
        var sagaIndexes = (await sagaCollection.Indexes.ListAsync()).ToList();

        var uxOrderId = sagaIndexes.FirstOrDefault(idx => idx["name"] == "ux_orderId");
        Assert.NotNull(uxOrderId);
        Assert.True(uxOrderId.GetValue("unique", false).AsBoolean);
        Assert.Equal(1, uxOrderId["key"]["orderId"].AsInt32);

        var ixCurrentStateExpiresAt = sagaIndexes.FirstOrDefault(idx => idx["name"] == "ix_currentState_expiresAt");
        Assert.NotNull(ixCurrentStateExpiresAt);
        Assert.Equal(1, ixCurrentStateExpiresAt["key"]["currentState"].AsInt32);
        Assert.Equal(1, ixCurrentStateExpiresAt["key"]["expiresAt"].AsInt32);

        // 6. Verify OrderCompensations Indexes
        var compCollection = _database.GetCollection<OrderCompensation>(OrderDbInitializer.OrderCompensationsCollectionName);
        var compIndexes = (await compCollection.Indexes.ListAsync()).ToList();

        var ixStatusNextRetry = compIndexes.FirstOrDefault(idx => idx["name"] == "ix_status_nextRetryAt");
        Assert.NotNull(ixStatusNextRetry);
        Assert.Equal(1, ixStatusNextRetry["key"]["status"].AsInt32);
        Assert.Equal(1, ixStatusNextRetry["key"]["nextRetryAt"].AsInt32);

        // 7. Verify IdempotencyRecords Indexes
        var idemCollection = _database.GetCollection<IdempotencyRecord>(OrderDbInitializer.IdempotencyRecordsCollectionName);
        var idemIndexes = (await idemCollection.Indexes.ListAsync()).ToList();

        var uxKey = idemIndexes.FirstOrDefault(idx => idx["name"] == "ux_key");
        Assert.NotNull(uxKey);
        Assert.True(uxKey.GetValue("unique", false).AsBoolean);
        Assert.Equal(1, uxKey["key"]["key"].AsInt32);

        var ttlExpiresAt = idemIndexes.FirstOrDefault(idx => idx["name"] == "ttl_expiresAt");
        Assert.NotNull(ttlExpiresAt);
        Assert.Equal(1, ttlExpiresAt["key"]["expiresAt"].AsInt32);
        Assert.Equal(0, ttlExpiresAt["expireAfterSeconds"].AsInt32); // TimeSpan.Zero
    }

    [Fact]
    public async Task UniqueIndexes_Empirically_Enforce_Uniqueness()
    {
        await OrderDbInitializer.InitializeAsync(_database);

        // 1. Carts ux_userId duplicate rejection
        var carts = _database.GetCollection<Cart>(OrderDbInitializer.CartsCollectionName);
        var testUserId = "stress_user_" + Guid.NewGuid();
        await carts.InsertOneAsync(new Cart { UserId = testUserId });
        var duplicateCartEx = await Record.ExceptionAsync(() => carts.InsertOneAsync(new Cart { UserId = testUserId }));
        Assert.IsType<MongoWriteException>(duplicateCartEx);

        // 2. Orders ux_orderCode duplicate rejection
        var orders = _database.GetCollection<Order>(OrderDbInitializer.OrdersCollectionName);
        var testCode = "ADI-STRESS-" + Guid.NewGuid().ToString()[..8];
        await orders.InsertOneAsync(new Order { OrderCode = testCode, UserId = "user1" });
        var duplicateOrderEx = await Record.ExceptionAsync(() => orders.InsertOneAsync(new Order { OrderCode = testCode, UserId = "user2" }));
        Assert.IsType<MongoWriteException>(duplicateOrderEx);

        // 3. OrderSagaStates ux_orderId duplicate rejection
        var sagas = _database.GetCollection<OrderSagaState>(OrderDbInitializer.OrderSagaStatesCollectionName);
        var testOrderId = "order_stress_" + Guid.NewGuid();
        await sagas.InsertOneAsync(new OrderSagaState { OrderId = testOrderId, CurrentState = OrderStatusConstants.AwaitingPayment });
        var duplicateSagaEx = await Record.ExceptionAsync(() => sagas.InsertOneAsync(new OrderSagaState { OrderId = testOrderId, CurrentState = OrderStatusConstants.AwaitingPayment }));
        Assert.IsType<MongoWriteException>(duplicateSagaEx);

        // 4. IdempotencyRecords ux_key duplicate rejection
        var idems = _database.GetCollection<IdempotencyRecord>(OrderDbInitializer.IdempotencyRecordsCollectionName);
        var testKey = "idem_stress_" + Guid.NewGuid();
        await idems.InsertOneAsync(new IdempotencyRecord { Key = testKey, UserId = "u1" });
        var duplicateIdemEx = await Record.ExceptionAsync(() => idems.InsertOneAsync(new IdempotencyRecord { Key = testKey, UserId = "u2" }));
        Assert.IsType<MongoWriteException>(duplicateIdemEx);
    }
}
