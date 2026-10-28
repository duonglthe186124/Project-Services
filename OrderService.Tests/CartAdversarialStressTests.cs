using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Controllers;
using OrderService.Dtos;
using OrderService.Models;
using OrderService.Services;
using Xunit;

namespace OrderService.Tests;

/// <summary>
/// Empirical adversarial stress tests written by Challenger 2 for Milestone 2 Iteration 2.
/// Stress-tests edge cases for non-hex 24-character ProductIds, reflection-based attribute verification,
/// and Validator.TryValidateObject validation logic.
/// </summary>
public class CartAdversarialStressTests
{
    private const string ValidObjectId = "507f1f77bcf86cd799439011";

    public static readonly string[] MalformedOrNonHexProductIds =
    [
        "zzzzzzzzzzzzzzzzzzzzzzzz",
        "12345678901234567890123g",
        "507f1f77bcf86cd79943901z",
        "507f1f77bcf86cd79943901G",
        "507f1f77bcf8-cd799439011",
        "507f1f77bcf8!cd799439011",
        "507f1f77bcf8 cd799439011",
        "507f1f77bcf8\tcd799439011",
        "507f1f77bcf8\ncd799439011",
        "XXXXXXXXXXXXXXXXXXXXXXXX",
        "123456789012345678901234 ", // trailing space within length or stripped
        " 123456789012345678901234",
        "12345678901234567890123",  // 23 chars
        "1234567890123456789012345", // 25 chars
        "",
        "   "
    ];

    private class MockCartService : ICartService
    {
        public Task<Cart> GetOrCreateCartAsync(string userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new Cart { UserId = userId, Items = new List<CartItem>() });

        public Task<Cart> AddItemToCartAsync(string userId, AddToCartRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new Cart { UserId = userId, Items = new List<CartItem>() });

        public Task<Cart?> UpdateItemQuantityAsync(string userId, string productId, int size, int quantity, CancellationToken cancellationToken = default)
            => Task.FromResult<Cart?>(new Cart { UserId = userId, Items = new List<CartItem>() });

        public Task<Cart?> RemoveItemFromCartAsync(string userId, string productId, int size, CancellationToken cancellationToken = default)
            => Task.FromResult<Cart?>(new Cart { UserId = userId, Items = new List<CartItem>() });

        public Task<Cart> ClearCartAsync(string userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new Cart { UserId = userId, Items = new List<CartItem>() });
    }

    private static CartsController CreateController()
    {
        var controller = new CartsController(new MockCartService(), NullLogger<CartsController>.Instance);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test_customer"),
            new Claim(ClaimTypes.Role, "Customer")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
        return controller;
    }

    #region Task Item 1: Non-Hex 24-character ProductIds rejected with HTTP 400 Bad Request

    [Theory]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("12345678901234567890123g")]
    [InlineData("507f1f77bcf86cd79943901z")]
    [InlineData("507f1f77bcf86cd79943901G")]
    [InlineData("507f1f77bcf8-cd799439011")]
    [InlineData("507f1f77bcf8!cd799439011")]
    [InlineData("507f1f77bcf8 cd799439011")]
    [InlineData("XXXXXXXXXXXXXXXXXXXXXXXX")]
    public async Task PostAddItem_NonHex24CharProductId_Returns400BadRequest(string nonHexId)
    {
        var controller = CreateController();
        var request = new AddToCartRequest(nonHexId, 42, 1);

        var result = await controller.AddItem(request, CancellationToken.None);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequestResult.StatusCode);
    }

    [Theory]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("12345678901234567890123g")]
    [InlineData("507f1f77bcf86cd79943901z")]
    [InlineData("507f1f77bcf86cd79943901G")]
    [InlineData("507f1f77bcf8-cd799439011")]
    [InlineData("507f1f77bcf8!cd799439011")]
    [InlineData("507f1f77bcf8 cd799439011")]
    [InlineData("XXXXXXXXXXXXXXXXXXXXXXXX")]
    public async Task PutUpdateItemQuantity_NonHex24CharProductId_Returns400BadRequest(string nonHexId)
    {
        var controller = CreateController();
        var request = new UpdateCartItemRequest(5);

        var result = await controller.UpdateItemQuantity(nonHexId, 42, request, CancellationToken.None);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequestResult.StatusCode);
    }

    [Theory]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("12345678901234567890123g")]
    [InlineData("507f1f77bcf86cd79943901z")]
    [InlineData("507f1f77bcf86cd79943901G")]
    [InlineData("507f1f77bcf8-cd799439011")]
    [InlineData("507f1f77bcf8!cd799439011")]
    [InlineData("507f1f77bcf8 cd799439011")]
    [InlineData("XXXXXXXXXXXXXXXXXXXXXXXX")]
    public async Task DeleteRemoveItem_NonHex24CharProductId_Returns400BadRequest(string nonHexId)
    {
        var controller = CreateController();

        var result = await controller.RemoveItem(nonHexId, 42, CancellationToken.None);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequestResult.StatusCode);
    }

    #endregion

    #region Task Item 2: Reflection & Validator.TryValidateObject Verification

    [Fact]
    public void Reflection_AddToCartRequest_Properties_Carry_ValidationAttributes()
    {
        var dtoType = typeof(AddToCartRequest);

        // ProductId property check
        var productProp = dtoType.GetProperty(nameof(AddToCartRequest.ProductId));
        Assert.NotNull(productProp);
        var requiredAttr = productProp.GetCustomAttribute<RequiredAttribute>();
        Assert.NotNull(requiredAttr);
        var mongoAttr = productProp.GetCustomAttribute<MongoObjectIdAttribute>();
        Assert.NotNull(mongoAttr);
        var regexAttr = productProp.GetCustomAttribute<RegularExpressionAttribute>();
        Assert.NotNull(regexAttr);
        Assert.Equal(@"^[0-9a-fA-F]{24}$", regexAttr.Pattern);

        // Size property check
        var sizeProp = dtoType.GetProperty(nameof(AddToCartRequest.Size));
        Assert.NotNull(sizeProp);
        var sizeRangeAttr = sizeProp.GetCustomAttribute<RangeAttribute>();
        Assert.NotNull(sizeRangeAttr);
        Assert.Equal(30, (int)sizeRangeAttr.Minimum);
        Assert.Equal(55, (int)sizeRangeAttr.Maximum);

        // Quantity property check
        var qtyProp = dtoType.GetProperty(nameof(AddToCartRequest.Quantity));
        Assert.NotNull(qtyProp);
        var qtyRangeAttr = qtyProp.GetCustomAttribute<RangeAttribute>();
        Assert.NotNull(qtyRangeAttr);
        Assert.Equal(1, (int)qtyRangeAttr.Minimum);
        Assert.Equal(100, (int)qtyRangeAttr.Maximum);
    }

    [Fact]
    public void Reflection_UpdateCartItemRequest_Property_Carries_RangeAttribute()
    {
        var dtoType = typeof(UpdateCartItemRequest);
        var qtyProp = dtoType.GetProperty(nameof(UpdateCartItemRequest.Quantity));
        Assert.NotNull(qtyProp);

        var rangeAttr = qtyProp.GetCustomAttribute<RangeAttribute>();
        Assert.NotNull(rangeAttr);
        Assert.Equal(1, (int)rangeAttr.Minimum);
        Assert.Equal(100, (int)rangeAttr.Maximum);
    }

    [Theory]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzz", 42, 1, nameof(AddToCartRequest.ProductId))]
    [InlineData("12345678901234567890123g", 42, 1, nameof(AddToCartRequest.ProductId))]
    [InlineData("short_id", 42, 1, nameof(AddToCartRequest.ProductId))]
    [InlineData(ValidObjectId, 29, 1, nameof(AddToCartRequest.Size))]
    [InlineData(ValidObjectId, 56, 1, nameof(AddToCartRequest.Size))]
    [InlineData(ValidObjectId, 42, 0, nameof(AddToCartRequest.Quantity))]
    [InlineData(ValidObjectId, 42, 101, nameof(AddToCartRequest.Quantity))]
    public void Validator_TryValidateObject_AddToCartRequest_DetectsInvalidProperties(
        string productId, int size, int quantity, string expectedFailedProperty)
    {
        var model = new AddToCartRequest(productId, size, quantity);
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model);

        bool isValid = Validator.TryValidateObject(model, context, results, validateAllProperties: true);

        Assert.False(isValid, $"Expected model with ProductId={productId}, Size={size}, Quantity={quantity} to fail validation.");
        Assert.Contains(results, r => r.MemberNames.Contains(expectedFailedProperty));
    }

    [Fact]
    public void Validator_TryValidateObject_AddToCartRequest_ValidData_Passes()
    {
        var model = new AddToCartRequest(ValidObjectId, 42, 2);
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model);

        bool isValid = Validator.TryValidateObject(model, context, results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(999)]
    public void Validator_TryValidateObject_UpdateCartItemRequest_DetectsInvalidQuantity(int quantity)
    {
        var model = new UpdateCartItemRequest(quantity);
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model);

        bool isValid = Validator.TryValidateObject(model, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateCartItemRequest.Quantity)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void Validator_TryValidateObject_UpdateCartItemRequest_ValidQuantity_Passes(int quantity)
    {
        var model = new UpdateCartItemRequest(quantity);
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model);

        bool isValid = Validator.TryValidateObject(model, context, results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    #endregion
}
