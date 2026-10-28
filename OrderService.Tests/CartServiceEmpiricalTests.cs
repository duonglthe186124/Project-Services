using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OrderService.Controllers;
using OrderService.Data;
using OrderService.Dtos;
using OrderService.Models;
using OrderService.Services;
using Xunit;

namespace OrderService.Tests;

public class CartServiceEmpiricalTests
{
    private static readonly string ConnectionString = TestMongoConfig.GetConnectionString();
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<Cart> _cartsCollection;

    public CartServiceEmpiricalTests()
    {
        var client = new MongoClient(ConnectionString);
        // Use an isolated database for Challenger M2 tests
        _database = client.GetDatabase("OrderDb_Challenger_M2");
        _cartsCollection = _database.GetCollection<Cart>(OrderDbInitializer.CartsCollectionName);
    }

    private async Task EnsureIndexesAsync()
    {
        await OrderDbInitializer.InitializeAsync(_database);
    }

    private class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler? _handler;

        public MockHttpClientFactory(HttpMessageHandler? handler = null)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return _handler != null 
                ? new HttpClient(_handler) { BaseAddress = new Uri("http://localhost:5000/") }
                : new HttpClient(new DummyFallbackHandler()) { BaseAddress = new Uri("http://localhost:5000/") };
        }
    }

    private class DummyFallbackHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // By default simulate Catalog offline so CartService uses graceful fallback
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private class CustomHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public CustomHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task GetOrCreateCartAsync_Returns_EmptyCart_When_New()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_new_" + Guid.NewGuid();

        var cart = await service.GetOrCreateCartAsync(userId);

        Assert.NotNull(cart);
        Assert.Equal(userId, cart.UserId);
        Assert.Empty(cart.Items);
        Assert.Equal(0, cart.TotalItems);
        Assert.Equal(0m, cart.SubTotal);
    }

    [Fact]
    public async Task GetOrCreateCartAsync_Concurrent_Race_On_Unique_ux_userId_Does_Not_Crash()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var testUserId = "race_user_" + Guid.NewGuid();

        // Launch 20 concurrent threads trying to GetOrCreateCart for the exact same user
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await service.GetOrCreateCartAsync(testUserId)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Every thread should have received a valid cart with testUserId
        Assert.All(results, cart =>
        {
            Assert.NotNull(cart);
            Assert.Equal(testUserId, cart.UserId);
        });

        // Exactly one document must exist in MongoDB
        var count = await _cartsCollection.CountDocumentsAsync(c => c.UserId == testUserId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddItemToCartAsync_Appends_New_Item_Cleanly()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_append_" + Guid.NewGuid();

        var req1 = new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 2,
            ProductName: "Ultraboost Light",
            UnitPrice: 3500000m
        );

        var req2 = new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c002",
            Size: 43,
            Quantity: 1,
            ProductName: "Forum Low",
            UnitPrice: 2200000m
        );

        // Add item 1
        var cart1 = await service.AddItemToCartAsync(userId, req1);
        Assert.Single(cart1.Items);
        Assert.Equal(2, cart1.TotalItems);
        Assert.Equal(7000000m, cart1.SubTotal);

        // Add item 2
        var cart2 = await service.AddItemToCartAsync(userId, req2);
        Assert.Equal(2, cart2.Items.Count);
        Assert.Equal(3, cart2.TotalItems);
        Assert.Equal(9200000m, cart2.SubTotal);
    }

    [Fact]
    public async Task AddItemToCartAsync_Existing_Item_Increments_Quantity_Without_Duplicates()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_inc_" + Guid.NewGuid();

        var req = new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 2,
            ProductName: "Ultraboost Light",
            UnitPrice: 3500000m
        );

        // Add first time (qty 2)
        var cart1 = await service.AddItemToCartAsync(userId, req);
        Assert.Single(cart1.Items);
        Assert.Equal(2, cart1.Items[0].Quantity);

        // Add second time with same ProductId and Size (qty 3)
        var reqAdd = req with { Quantity = 3 };
        var cart2 = await service.AddItemToCartAsync(userId, reqAdd);

        // Must still be 1 line item, quantity incremented to 5
        Assert.Single(cart2.Items);
        Assert.Equal(5, cart2.Items[0].Quantity);
        Assert.Equal(5, cart2.TotalItems);
        Assert.Equal(17500000m, cart2.SubTotal);
    }

    [Fact]
    public async Task AddItemToCartAsync_Concurrent_Race_On_Same_Item_Prevents_Duplicate_Lines()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_race_item_" + Guid.NewGuid();

        var req = new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 1,
            ProductName: "Ultraboost Light",
            UnitPrice: 3000000m
        );

        // 10 concurrent requests adding the same item simultaneously into an initially empty cart
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () => await service.AddItemToCartAsync(userId, req)))
            .ToArray();

        await Task.WhenAll(tasks);

        var finalCart = await service.GetOrCreateCartAsync(userId);

        // CRITICAL CHECK: Exactly 1 line item in cart, quantity must be exactly 10
        Assert.Single(finalCart.Items);
        Assert.Equal(42, finalCart.Items[0].Size);
        Assert.Equal(10, finalCart.Items[0].Quantity);
        Assert.Equal(10, finalCart.TotalItems);
        Assert.Equal(30000000m, finalCart.SubTotal);
    }

    [Fact]
    public async Task AddItemToCartAsync_Concurrent_Race_On_Different_Items_Appends_All()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_race_multi_" + Guid.NewGuid();

        // 8 concurrent requests adding 8 different items
        var tasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(async () =>
            {
                var req = new AddToCartRequest(
                    ProductId: $"65f1a2b3c4d5e6f7a8b9c00{i}",
                    Size: 40 + i,
                    Quantity: 1,
                    ProductName: $"Shoe #{i}",
                    UnitPrice: 1000000m
                );
                return await service.AddItemToCartAsync(userId, req);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        var finalCart = await service.GetOrCreateCartAsync(userId);

        // Must have all 8 items
        Assert.Equal(8, finalCart.Items.Count);
        Assert.Equal(8, finalCart.TotalItems);
        Assert.Equal(8000000m, finalCart.SubTotal);
    }

    [Fact]
    public async Task UpdateItemQuantityAsync_Updates_Quantity_And_SubTotal()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_update_" + Guid.NewGuid();

        await service.AddItemToCartAsync(userId, new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 2,
            UnitPrice: 2000000m
        ));

        var updatedCart = await service.UpdateItemQuantityAsync(userId, "65f1a2b3c4d5e6f7a8b9c001", 42, 5);

        Assert.NotNull(updatedCart);
        Assert.Single(updatedCart.Items);
        Assert.Equal(5, updatedCart.Items[0].Quantity);
        Assert.Equal(5, updatedCart.TotalItems);
        Assert.Equal(10000000m, updatedCart.SubTotal);
    }

    [Fact]
    public async Task UpdateItemQuantityAsync_NonExistent_Item_Returns_Null_Gracefully()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_nonexist_" + Guid.NewGuid();

        await service.GetOrCreateCartAsync(userId);

        // Item does not exist in cart
        var res = await service.UpdateItemQuantityAsync(userId, "65f1a2b3c4d5e6f7a8b9c999", 42, 5);
        Assert.Null(res);

        // Wrong size for existing item
        await service.AddItemToCartAsync(userId, new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 1
        ));

        var resWrongSize = await service.UpdateItemQuantityAsync(userId, "65f1a2b3c4d5e6f7a8b9c001", 43, 5);
        Assert.Null(resWrongSize);
    }

    [Fact]
    public async Task UpdateItemQuantityAsync_Zero_Or_Negative_Triggers_Removal()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_zero_qty_" + Guid.NewGuid();

        await service.AddItemToCartAsync(userId, new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 2
        ));

        // Update quantity to 0
        var cartAfterZero = await service.UpdateItemQuantityAsync(userId, "65f1a2b3c4d5e6f7a8b9c001", 42, 0);
        Assert.NotNull(cartAfterZero);
        Assert.Empty(cartAfterZero.Items);
        Assert.Equal(0, cartAfterZero.TotalItems);
        Assert.Equal(0m, cartAfterZero.SubTotal);
    }

    [Fact]
    public async Task RemoveItemFromCartAsync_Removes_Item_And_Returns_Cart()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_remove_" + Guid.NewGuid();

        await service.AddItemToCartAsync(userId, new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 1
        ));
        await service.AddItemToCartAsync(userId, new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c002",
            Size: 43,
            Quantity: 1
        ));

        var cartAfterRemove = await service.RemoveItemFromCartAsync(userId, "65f1a2b3c4d5e6f7a8b9c001", 42);
        Assert.NotNull(cartAfterRemove);
        Assert.Single(cartAfterRemove.Items);
        Assert.Equal("65f1a2b3c4d5e6f7a8b9c002", cartAfterRemove.Items[0].ProductId);

        // Removing non-existent item returns null
        var removeAgain = await service.RemoveItemFromCartAsync(userId, "65f1a2b3c4d5e6f7a8b9c001", 42);
        Assert.Null(removeAgain);
    }

    [Fact]
    public async Task ClearCartAsync_Resets_Items_To_Empty()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_clear_" + Guid.NewGuid();

        await service.AddItemToCartAsync(userId, new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 3,
            UnitPrice: 1000000m
        ));

        var clearedCart = await service.ClearCartAsync(userId);
        Assert.NotNull(clearedCart);
        Assert.Empty(clearedCart.Items);
        Assert.Equal(0, clearedCart.TotalItems);
        Assert.Equal(0m, clearedCart.SubTotal);

        // Verify in DB directly
        var dbCart = await _cartsCollection.Find(c => c.UserId == userId).FirstOrDefaultAsync();
        Assert.NotNull(dbCart);
        Assert.Empty(dbCart.Items);
    }

    [Fact]
    public async Task ClearCartAsync_When_No_Cart_Existed_Creates_And_Returns_Empty_Cart()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_clear_empty_" + Guid.NewGuid();

        var cart = await service.ClearCartAsync(userId);
        Assert.NotNull(cart);
        Assert.Equal(userId, cart.UserId);
        Assert.Empty(cart.Items);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(56)]
    public async Task AddItemToCartAsync_Invalid_Size_Throws_ArgumentOutOfRangeException(int invalidSize)
    {
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var req = new AddToCartRequest("65f1a2b3c4d5e6f7a8b9c001", invalidSize, 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddItemToCartAsync("user1", req));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public async Task AddItemToCartAsync_Invalid_Quantity_Throws_ArgumentOutOfRangeException(int invalidQuantity)
    {
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var req = new AddToCartRequest("65f1a2b3c4d5e6f7a8b9c001", 42, invalidQuantity);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddItemToCartAsync("user1", req));
    }

    [Fact]
    public async Task Catalog_Integration_NotFound_Throws_KeyNotFoundException()
    {
        var handler = new CustomHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var factory = new MockHttpClientFactory(handler);
        var service = new CartService(_database, factory, NullLogger<CartService>.Instance);

        var req = new AddToCartRequest("65f1a2b3c4d5e6f7a8b9c001", 42, 1);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AddItemToCartAsync("user1", req));
    }

    [Fact]
    public async Task Catalog_Integration_Unsupported_Size_Throws_ArgumentException()
    {
        var catalogJson = JsonSerializer.Serialize(new
        {
            Id = "65f1a2b3c4d5e6f7a8b9c001",
            Name = "Superstar",
            Category = "Originals",
            Price = 2500000m,
            Sizes = new[]
            {
                new { Size = 40, Quantity = 10, ReservedQuantity = 0 },
                new { Size = 41, Quantity = 5, ReservedQuantity = 0 }
            }
        });

        var handler = new CustomHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(catalogJson, System.Text.Encoding.UTF8, "application/json")
        });
        var factory = new MockHttpClientFactory(handler);
        var service = new CartService(_database, factory, NullLogger<CartService>.Instance);

        // Request size 42, but catalog only has 40 and 41
        var req = new AddToCartRequest("65f1a2b3c4d5e6f7a8b9c001", 42, 1);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.AddItemToCartAsync("user1", req));
        Assert.Contains("hiện không có size 42", ex.Message);
    }

    [Fact]
    public async Task CartsController_Returns_401_When_UserClaim_Missing()
    {
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var controller = new CartsController(service, NullLogger<CartsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext() // No claims
            }
        };

        var getResult = await controller.GetMyCart(CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(getResult.Result);

        var addResult = await controller.AddItem(new AddToCartRequest("65f1a2b3c4d5e6f7a8b9c001", 42, 1), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(addResult.Result);

        var updateResult = await controller.UpdateItemQuantity("65f1a2b3c4d5e6f7a8b9c001", 42, new UpdateCartItemRequest(2), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(updateResult.Result);

        var removeResult = await controller.RemoveItem("65f1a2b3c4d5e6f7a8b9c001", 42, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(removeResult.Result);

        var clearResult = await controller.ClearCart(CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(clearResult.Result);
    }

    [Fact]
    public async Task CartsController_Update_Or_Remove_NonExistent_Returns_404()
    {
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var testUserId = "ctrl_user_" + Guid.NewGuid();

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, testUserId) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var controller = new CartsController(service, NullLogger<CartsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        // Update non-existent item
        var updateResult = await controller.UpdateItemQuantity("65f1a2b3c4d5e6f7a8b9c999", 42, new UpdateCartItemRequest(2), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(updateResult.Result);

        // Remove non-existent item
        var removeResult = await controller.RemoveItem("65f1a2b3c4d5e6f7a8b9c999", 42, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(removeResult.Result);
    }

    [Fact]
    public async Task RemoveItemFromCartAsync_Concurrent_Race_Returns_Cleanly_Without_Exceptions()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_remove_race_" + Guid.NewGuid();

        await service.AddItemToCartAsync(userId, new AddToCartRequest(
            ProductId: "65f1a2b3c4d5e6f7a8b9c001",
            Size: 42,
            Quantity: 2
        ));

        // 10 concurrent threads attempt to remove the same item
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () => await service.RemoveItemFromCartAsync(userId, "65f1a2b3c4d5e6f7a8b9c001", 42)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // At least one thread should succeed, subsequent threads may return null or cart without item
        var nonNullResults = results.Where(r => r != null).ToList();
        Assert.NotEmpty(nonNullResults);

        // Final cart in DB must not have the item
        var finalCart = await service.GetOrCreateCartAsync(userId);
        Assert.Empty(finalCart.Items);
    }

    [Fact]
    public async Task ClearCartAsync_Concurrent_With_Add_Does_Not_Corrupt_Cart()
    {
        await EnsureIndexesAsync();
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_clear_race_" + Guid.NewGuid();

        // 5 threads add items, 5 threads clear cart
        var addTasks = Enumerable.Range(0, 5)
            .Select(i => Task.Run(async () =>
            {
                await Task.Delay(Random.Shared.Next(1, 10));
                await service.AddItemToCartAsync(userId, new AddToCartRequest(
                    ProductId: $"65f1a2b3c4d5e6f7a8b9c00{i}",
                    Size: 40 + i,
                    Quantity: 1
                ));
            }));

        var clearTasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(async () =>
            {
                await Task.Delay(Random.Shared.Next(1, 10));
                await service.ClearCartAsync(userId);
            }));

        await Task.WhenAll(addTasks.Concat(clearTasks));

        // After all finish, cart should be in a valid consistent state
        var cart = await service.GetOrCreateCartAsync(userId);
        Assert.NotNull(cart);
        Assert.Equal(cart.Items.Sum(i => i.Quantity), cart.TotalItems);
        Assert.Equal(cart.Items.Sum(i => i.LineTotal), cart.SubTotal);
    }

    [Fact]
    public async Task InputValidation_Null_And_Empty_Arguments_Throw_Expected_Exceptions()
    {
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);

        // GetOrCreateCartAsync
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetOrCreateCartAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetOrCreateCartAsync("   "));

        // AddItemToCartAsync
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddItemToCartAsync("", new AddToCartRequest("65f1a2b3c4d5e6f7a8b9c001", 42, 1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.AddItemToCartAsync("user1", null!));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddItemToCartAsync("user1", new AddToCartRequest("", 42, 1)));

        // UpdateItemQuantityAsync
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateItemQuantityAsync("", "65f1a2b3c4d5e6f7a8b9c001", 42, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateItemQuantityAsync("user1", "", 42, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.UpdateItemQuantityAsync("user1", "65f1a2b3c4d5e6f7a8b9c001", 20, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.UpdateItemQuantityAsync("user1", "65f1a2b3c4d5e6f7a8b9c001", 42, 101));

        // RemoveItemFromCartAsync
        await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveItemFromCartAsync("", "65f1a2b3c4d5e6f7a8b9c001", 42));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveItemFromCartAsync("user1", "", 42));

        // ClearCartAsync
        await Assert.ThrowsAsync<ArgumentException>(() => service.ClearCartAsync(""));
    }
}
