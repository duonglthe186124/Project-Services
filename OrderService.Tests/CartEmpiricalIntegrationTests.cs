using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
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

public class CartEmpiricalIntegrationTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestMongoConfig.GetConnectionString();
    private readonly IMongoDatabase _database;
    private readonly string _testDbName;
    private readonly MongoClient _mongoClient;

    public CartEmpiricalIntegrationTests()
    {
        _testDbName = "OrderDb_Cart_Audit_" + Guid.NewGuid().ToString("N")[..8];
        _mongoClient = new MongoClient(ConnectionString);
        _database = _mongoClient.GetDatabase(_testDbName);
    }

    public async Task InitializeAsync()
    {
        await OrderDbInitializer.InitializeAsync(_database);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _mongoClient.DropDatabaseAsync(_testDbName);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private class MockHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            // Returns a client with a fake handler that simulates offline catalog or mock catalog
            var handler = new FakeCatalogHttpMessageHandler();
            return new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:5000/")
            };
        }
    }

    private class FakeCatalogHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Simulate 404 for unknown product id "000000000000000000000000"
            if (request.RequestUri?.AbsolutePath.EndsWith("000000000000000000000000") == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // Return valid product for 6ad318f7691a1e702d881190
            var json = """
            {
                "id": "6ad318f7691a1e702d881190",
                "name": "Ultraboost Light",
                "category": "Running",
                "price": 3800000,
                "salePrice": null,
                "thumbnailUrl": "https://assets.adidas.com/ultraboost.jpg",
                "sizes": [
                    { "size": 40, "quantity": 10, "reservedQuantity": 0 },
                    { "size": 41, "quantity": 10, "reservedQuantity": 0 },
                    { "size": 42, "quantity": 10, "reservedQuantity": 0 }
                ]
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task CartService_Interacts_With_Real_MongoDB_Carts_Collection()
    {
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "user_atlas_test_" + Guid.NewGuid();

        // 1. GetOrCreateCart creates physical document in MongoDB
        var cart = await service.GetOrCreateCartAsync(userId);
        Assert.NotNull(cart);
        Assert.Equal(userId, cart.UserId);
        Assert.Empty(cart.Items);

        // Verify physically in MongoDB collection
        var rawCollection = _database.GetCollection<BsonDocument>(OrderDbInitializer.CartsCollectionName);
        var doc = await rawCollection.Find(Builders<BsonDocument>.Filter.Eq("userId", userId)).FirstOrDefaultAsync();
        Assert.NotNull(doc);
        Assert.Equal(userId, doc["userId"].AsString);
        Assert.Empty(doc["items"].AsBsonArray);

        // 2. AddItemToCart inserts item via Catalog snapshot
        var addRequest = new AddToCartRequest(
            ProductId: "6ad318f7691a1e702d881190",
            Size: 42,
            Quantity: 2
        );
        var updatedCart = await service.AddItemToCartAsync(userId, addRequest);
        Assert.Single(updatedCart.Items);
        Assert.Equal("6ad318f7691a1e702d881190", updatedCart.Items[0].ProductId);
        Assert.Equal(42, updatedCart.Items[0].Size);
        Assert.Equal(2, updatedCart.Items[0].Quantity);
        Assert.Equal("Ultraboost Light", updatedCart.Items[0].ProductName);
        Assert.Equal(3800000m, updatedCart.Items[0].UnitPrice);
        Assert.Equal(7600000m, updatedCart.Items[0].LineTotal);

        // Verify physically in MongoDB
        doc = await rawCollection.Find(Builders<BsonDocument>.Filter.Eq("userId", userId)).FirstOrDefaultAsync();
        Assert.NotNull(doc);
        Assert.Single(doc["items"].AsBsonArray);
        var rawItem = doc["items"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal("6ad318f7691a1e702d881190", rawItem["productId"].AsString);
        Assert.Equal(2, rawItem["quantity"].AsInt32);

        // 3. Add same item increments quantity atomically ($inc)
        var addAgainRequest = new AddToCartRequest(
            ProductId: "6ad318f7691a1e702d881190",
            Size: 42,
            Quantity: 3
        );
        var cartAfterInc = await service.AddItemToCartAsync(userId, addAgainRequest);
        Assert.Single(cartAfterInc.Items);
        Assert.Equal(5, cartAfterInc.Items[0].Quantity);

        // 4. Update quantity directly
        var cartAfterUpdate = await service.UpdateItemQuantityAsync(userId, "6ad318f7691a1e702d881190", 42, 10);
        Assert.NotNull(cartAfterUpdate);
        Assert.Equal(10, cartAfterUpdate.Items[0].Quantity);

        // 5. Remove item
        var cartAfterRemove = await service.RemoveItemFromCartAsync(userId, "6ad318f7691a1e702d881190", 42);
        Assert.NotNull(cartAfterRemove);
        Assert.Empty(cartAfterRemove.Items);

        // Verify physically in MongoDB
        doc = await rawCollection.Find(Builders<BsonDocument>.Filter.Eq("userId", userId)).FirstOrDefaultAsync();
        Assert.NotNull(doc);
        Assert.Empty(doc["items"].AsBsonArray);

        // 6. Clear cart
        await service.AddItemToCartAsync(userId, addRequest);
        var cartCleared = await service.ClearCartAsync(userId);
        Assert.Empty(cartCleared.Items);

        doc = await rawCollection.Find(Builders<BsonDocument>.Filter.Eq("userId", userId)).FirstOrDefaultAsync();
        Assert.NotNull(doc);
        Assert.Empty(doc["items"].AsBsonArray);
    }

    [Fact]
    public async Task CartsController_Invokes_ICartService_And_Maps_Dtos_Correctly()
    {
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var controller = new CartsController(service, NullLogger<CartsController>.Instance);

        var testUserId = "user_ctrl_test_" + Guid.NewGuid();

        // Setup HttpContext with ClaimsPrincipal (Customer)
        var userClaims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, testUserId),
            new Claim(ClaimTypes.Role, "Customer")
        }, "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = userClaims }
        };

        // 1. GET /api/carts/my-cart
        var getResult = await controller.GetMyCart(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(getResult.Result);
        var cartDto = Assert.IsType<CartDto>(okResult.Value);
        Assert.Equal(testUserId, cartDto.UserId);
        Assert.Empty(cartDto.Items);

        // 2. POST /api/carts/items
        var addRequest = new AddToCartRequest("6ad318f7691a1e702d881190", 42, 1);
        var postResult = await controller.AddItem(addRequest, CancellationToken.None);
        var postOkResult = Assert.IsType<OkObjectResult>(postResult.Result);
        var postDto = Assert.IsType<CartDto>(postOkResult.Value);
        Assert.Single(postDto.Items);
        Assert.Equal(1, postDto.TotalItems);
        Assert.Equal(3800000m, postDto.SubTotal);

        // 3. PUT /api/carts/items/{productId}/{size}
        var updateRequest = new UpdateCartItemRequest(4);
        var putResult = await controller.UpdateItemQuantity("6ad318f7691a1e702d881190", 42, updateRequest, CancellationToken.None);
        var putOkResult = Assert.IsType<OkObjectResult>(putResult.Result);
        var putDto = Assert.IsType<CartDto>(putOkResult.Value);
        Assert.Equal(4, putDto.TotalItems);

        // 4. DELETE /api/carts/items/{productId}/{size}
        var deleteResult = await controller.RemoveItem("6ad318f7691a1e702d881190", 42, CancellationToken.None);
        var deleteOkResult = Assert.IsType<OkObjectResult>(deleteResult.Result);
        var deleteDto = Assert.IsType<CartDto>(deleteOkResult.Value);
        Assert.Empty(deleteDto.Items);

        // 5. DELETE /api/carts/clear
        await controller.AddItem(addRequest, CancellationToken.None);
        var clearResult = await controller.ClearCart(CancellationToken.None);
        var clearOkResult = Assert.IsType<OkObjectResult>(clearResult.Result);
        var clearDto = Assert.IsType<CartDto>(clearOkResult.Value);
        Assert.Empty(clearDto.Items);
    }

    [Fact]
    public async Task CartService_Enforces_Unique_User_Cart_Under_Race_Condition()
    {
        var service = new CartService(_database, new MockHttpClientFactory(), NullLogger<CartService>.Instance);
        var userId = "race_user_" + Guid.NewGuid();

        // Concurrently call GetOrCreateCartAsync 10 times
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => service.GetOrCreateCartAsync(userId))
            .ToList();

        var carts = await Task.WhenAll(tasks);

        // All should succeed and return the exact same cart instance or user id
        Assert.All(carts, c => Assert.Equal(userId, c.UserId));

        // In MongoDB collection, exactly ONE document must exist for this userId
        var count = await _database.GetCollection<Cart>(OrderDbInitializer.CartsCollectionName)
            .CountDocumentsAsync(Builders<Cart>.Filter.Eq(c => c.UserId, userId));

        Assert.Equal(1, count);
    }
}
