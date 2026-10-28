using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using OrderService.Models;
using Xunit;

namespace OrderService.Models.Tests;

public class ModelBsonSerializationTests
{
    [Fact]
    public void Cart_BsonSerialization_RoundTrip_Succeeds()
    {
        var cart = new Cart
        {
            Id = ObjectId.GenerateNewId().ToString(),
            UserId = "user_test_123",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = new List<CartItem>
            {
                new()
                {
                    ProductId = "prod_001",
                    ProductName = "Ultraboost Light",
                    ProductSku = "UB-2024-BLK",
                    Size = 42,
                    Quantity = 2,
                    UnitPrice = 4200000m,
                    ThumbnailUrl = "https://example.com/ub.jpg"
                }
            }
        };

        var bson = cart.ToBson();
        var deserialized = BsonSerializer.Deserialize<Cart>(bson);

        Assert.NotNull(deserialized);
        Assert.Equal(cart.Id, deserialized.Id);
        Assert.Equal(cart.UserId, deserialized.UserId);
        Assert.Single(deserialized.Items);
        Assert.Equal(42, deserialized.Items[0].Size);
        Assert.Equal(2, deserialized.Items[0].Quantity);
        Assert.Equal(4200000m, deserialized.Items[0].UnitPrice);
        Assert.Equal(8400000m, deserialized.Items[0].LineTotal);
        Assert.Equal(2, deserialized.TotalItems);
        Assert.Equal(8400000m, deserialized.SubTotal);

        var doc = cart.ToBsonDocument();
        Assert.Equal("user_test_123", doc["userId"].AsString);
        Assert.False(doc.Contains("totalItems"), "Calculated property TotalItems must be ignored in BSON");
        Assert.False(doc.Contains("subTotal"), "Calculated property SubTotal must be ignored in BSON");
    }

    [Fact]
    public void Order_BsonSerialization_RoundTrip_Succeeds()
    {
        var order = new Order
        {
            Id = ObjectId.GenerateNewId().ToString(),
            OrderCode = "ADI-20261019-ABCD",
            UserId = "user_customer_456",
            CustomerName = "Nguyen Van A",
            CustomerPhone = "0901234567",
            ShippingAddress = new ShippingAddress
            {
                AddressLine = "123 Le Loi",
                Ward = "Ben Nghe",
                District = "District 1",
                City = "Ho Chi Minh",
                Latitude = 10.7769,
                Longitude = 106.7009
            },
            Items = new List<OrderItem>
            {
                new()
                {
                    ProductId = "prod_predator",
                    ProductName = "Predator Elite FG",
                    Category = "Football",
                    Color = "Core Black / Solar Red",
                    Size = 41,
                    Quantity = 1,
                    UnitPrice = 6500000m,
                    LineTotal = 6500000m,
                    ThumbnailUrl = "https://example.com/pred.jpg"
                }
            },
            SubTotal = 6500000m,
            ShippingFee = 30000m,
            DiscountAmount = 100000m,
            TotalAmount = 6430000m,
            PaymentMethod = PaymentMethodConstants.Online_QR,
            OrderStatus = OrderStatusConstants.AwaitingPayment,
            IsHighValue = true,
            DeliveryOtpCode = "123456",
            SecuritySealCode = "SEAL-A1B2C3",
            IsVipBulk = false,
            RequiresManualApproval = false,
            IsManuallyApproved = false,
            ReservationId = "res_789",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var bson = order.ToBson();
        var deserialized = BsonSerializer.Deserialize<Order>(bson);

        Assert.NotNull(deserialized);
        Assert.Equal(order.OrderCode, deserialized.OrderCode);
        Assert.Equal(order.TotalAmount, deserialized.TotalAmount);
        Assert.True(deserialized.IsHighValue);
        Assert.Equal("123456", deserialized.DeliveryOtpCode);
        Assert.Equal("SEAL-A1B2C3", deserialized.SecuritySealCode);
        Assert.Equal(1, deserialized.TotalQuantity);

        var doc = order.ToBsonDocument();
        Assert.Equal("ADI-20261019-ABCD", doc["orderCode"].AsString);
        Assert.True(doc["shippingAddress"].IsBsonDocument);
        Assert.Equal("123 Le Loi", doc["shippingAddress"]["addressLine"].AsString);
        Assert.Equal(10.7769, doc["shippingAddress"]["latitude"].AsDouble);
        Assert.False(doc.Contains("totalQuantity"), "Calculated property TotalQuantity must be ignored");
    }

    [Fact]
    public void OrderSagaState_OCC_Version_And_AuditTrail_BsonRoundTrip()
    {
        var saga = new OrderSagaState
        {
            Id = ObjectId.GenerateNewId().ToString(),
            OrderId = ObjectId.GenerateNewId().ToString(),
            OrderCode = "ADI-20261019-XYZ1",
            UserId = "user_789",
            CurrentState = OrderStatusConstants.AwaitingPayment,
            Version = 1,
            HoldMinutes = 15,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15),
            CreatedAt = DateTime.UtcNow,
            AuditTrail = new List<AuditLogEntry>
            {
                new()
                {
                    FromState = "None",
                    ToState = OrderStatusConstants.AwaitingPayment,
                    TriggerAction = SagaTriggerConstants.CheckoutSuccess,
                    Timestamp = DateTime.UtcNow,
                    Actor = "user_789",
                    Notes = "Checkout created order with 15m hold"
                }
            }
        };

        var bson = saga.ToBson();
        var deserialized = BsonSerializer.Deserialize<OrderSagaState>(bson);

        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized.Version);
        Assert.Equal(OrderStatusConstants.AwaitingPayment, deserialized.CurrentState);
        Assert.Single(deserialized.AuditTrail);
        Assert.Equal(SagaTriggerConstants.CheckoutSuccess, deserialized.AuditTrail[0].TriggerAction);

        // Verify OCC increment
        deserialized.Version++;
        deserialized.CurrentState = OrderStatusConstants.PaymentVerified;
        deserialized.AuditTrail.Add(new AuditLogEntry
        {
            FromState = OrderStatusConstants.AwaitingPayment,
            ToState = OrderStatusConstants.PaymentVerified,
            TriggerAction = SagaTriggerConstants.PaymentCallbackSuccess,
            Actor = "System:PaymentWebhook"
        });

        var updatedDoc = deserialized.ToBsonDocument();
        Assert.Equal(2, updatedDoc["version"].AsInt32);
        Assert.Equal(OrderStatusConstants.PaymentVerified, updatedDoc["currentState"].AsString);
        Assert.Equal(2, updatedDoc["auditTrail"].AsBsonArray.Count);
    }

    [Fact]
    public void OrderCompensation_BsonRoundTrip_Succeeds()
    {
        var comp = new OrderCompensation
        {
            Id = ObjectId.GenerateNewId().ToString(),
            OrderId = "order_123",
            OrderCode = "ADI-20261019-COMP",
            Action = "ReleaseStock",
            Payload = "{\"orderId\":\"order_123\"}",
            Status = CompensationStatusConstants.Pending,
            RetryCount = 0,
            MaxRetries = 10,
            NextRetryAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        var bson = comp.ToBson();
        var deserialized = BsonSerializer.Deserialize<OrderCompensation>(bson);

        Assert.NotNull(deserialized);
        Assert.Equal("ReleaseStock", deserialized.Action);
        Assert.Equal(CompensationStatusConstants.Pending, deserialized.Status);
        Assert.Equal(10, deserialized.MaxRetries);
    }

    [Fact]
    public void IdempotencyRecord_TTL_And_Key_BsonRoundTrip_Succeeds()
    {
        var record = new IdempotencyRecord
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Key = "idem-key-999",
            UserId = "user_111",
            Endpoint = "/api/orders/checkout",
            RequestHash = "hash123",
            ResponseStatusCode = 201,
            ResponseBody = "{\"orderId\":\"ord_123\"}",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };

        var bson = record.ToBson();
        var deserialized = BsonSerializer.Deserialize<IdempotencyRecord>(bson);

        Assert.NotNull(deserialized);
        Assert.Equal("idem-key-999", deserialized.Key);
        Assert.Equal("idem-key-999", deserialized.IdempotencyKey);
        Assert.Equal(201, deserialized.ResponseStatusCode);

        var doc = record.ToBsonDocument();
        Assert.Equal("idem-key-999", doc["key"].AsString);
        Assert.False(doc.Contains("idempotencyKey"), "IdempotencyKey alias must be BsonIgnored");
    }

    [Fact]
    public void OrderEnums_Constants_And_Helpers_ValidateProperly()
    {
        Assert.True(OrderStatusConstants.IsTerminal(OrderStatusConstants.Completed));
        Assert.True(OrderStatusConstants.IsTerminal(OrderStatusConstants.Cancelled));
        Assert.True(OrderStatusConstants.IsTerminal(OrderStatusConstants.Expired));
        Assert.True(OrderStatusConstants.IsTerminal(OrderStatusConstants.LatePaymentDetected));
        Assert.False(OrderStatusConstants.IsTerminal(OrderStatusConstants.AwaitingPayment));
        Assert.False(OrderStatusConstants.IsTerminal(OrderStatusConstants.PaymentVerified));

        Assert.True(OrderStatusConstants.CanBeCancelledByCustomer(OrderStatusConstants.AwaitingPayment));
        Assert.False(OrderStatusConstants.CanBeCancelledByCustomer(OrderStatusConstants.WarehousePacking));

        Assert.True(PaymentMethodConstants.IsOnline(PaymentMethodConstants.Online_QR));
        Assert.True(PaymentMethodConstants.Online_Card == "Online_Card");
        Assert.True(PaymentMethodConstants.IsOnline(PaymentMethodConstants.EWallet));
        Assert.False(PaymentMethodConstants.IsOnline(PaymentMethodConstants.COD));

        Assert.Equal(5_000_000m, OrderTierConstants.HighValueThresholdAmount);
        Assert.Equal(15_000_000m, OrderTierConstants.VipBulkThresholdAmount);
        Assert.Equal(4, OrderTierConstants.VipBulkThresholdQuantity);
        Assert.Equal(2, OrderTierConstants.MaxActiveAwaitingOrdersPerUser);
        Assert.Equal(15, OrderTierConstants.DefaultHoldMinutes);
    }
}
