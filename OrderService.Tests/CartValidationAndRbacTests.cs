using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Controllers;
using OrderService.Dtos;
using OrderService.Models;
using OrderService.Services;
using Xunit;

namespace OrderService.Tests;

/// <summary>
/// Empirical challenge test suite testing CartsController, CartDtos validation, and RBAC authorization.
/// Executed by Challenger 2 (Milestone 2 - Cart Domain & APIs).
/// </summary>
public class CartValidationAndRbacTests
{
    private const string ValidObjectId = "507f1f77bcf86cd799439011";
    private const string NonHex24Chars = "zzzzzzzzzzzzzzzzzzzzzzzz";
    private const string NonHexWithInvalidChar = "507f1f77bcf86cd79943901g";

    #region Helper & Test Fixtures

    private class FakeCartService : ICartService
    {
        public readonly Dictionary<string, Cart> Carts = new();

        public Task<Cart> GetOrCreateCartAsync(string userId, CancellationToken ct = default)
        {
            if (!Carts.TryGetValue(userId, out var cart))
            {
                cart = new Cart
                {
                    Id = "cart_" + Guid.NewGuid(),
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Items = new List<CartItem>()
                };
                Carts[userId] = cart;
            }
            return Task.FromResult(cart);
        }

        public Task<Cart> AddItemToCartAsync(string userId, AddToCartRequest request, CancellationToken ct = default)
        {
            var cart = GetOrCreateCartAsync(userId, ct).Result;
            var existing = cart.Items.FirstOrDefault(i => i.ProductId == request.ProductId && i.Size == request.Size);
            if (existing != null)
            {
                existing.Quantity += request.Quantity;
            }
            else
            {
                cart.Items.Add(new CartItem
                {
                    ProductId = request.ProductId,
                    ProductName = request.ProductName ?? "Adidas Shoes",
                    ProductSku = request.ProductSku ?? "ADI-42",
                    Size = request.Size,
                    Quantity = request.Quantity,
                    UnitPrice = request.UnitPrice ?? 1_000_000m,
                    ThumbnailUrl = request.ThumbnailUrl ?? ""
                });
            }
            cart.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult(cart);
        }

        public Task<Cart?> UpdateItemQuantityAsync(string userId, string productId, int size, int quantity, CancellationToken ct = default)
        {
            if (!Carts.TryGetValue(userId, out var cart))
                return Task.FromResult<Cart?>(null);

            var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.Size == size);
            if (item == null)
                return Task.FromResult<Cart?>(null);

            item.Quantity = quantity;
            cart.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult<Cart?>(cart);
        }

        public Task<Cart?> RemoveItemFromCartAsync(string userId, string productId, int size, CancellationToken ct = default)
        {
            if (!Carts.TryGetValue(userId, out var cart))
                return Task.FromResult<Cart?>(null);

            var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.Size == size);
            if (item == null)
                return Task.FromResult<Cart?>(null);

            cart.Items.Remove(item);
            cart.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult<Cart?>(cart);
        }

        public Task<Cart> ClearCartAsync(string userId, CancellationToken ct = default)
        {
            var cart = GetOrCreateCartAsync(userId, ct).Result;
            cart.Items.Clear();
            cart.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult(cart);
        }
    }

    private static CartsController CreateController(ICartService service, ClaimsPrincipal? user = null)
    {
        var controller = new CartsController(service, NullLogger<CartsController>.Instance);
        var httpContext = new DefaultHttpContext();
        if (user != null)
        {
            httpContext.User = user;
        }
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    private static ClaimsPrincipal CreateUserPrincipal(string userId, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        return new ClaimsPrincipal(identity);
    }

    #endregion

    #region 1. DTO Attribute Targeting (Verification of Property Target Fix)

    /// <summary>
    /// EMPIRICAL VERIFICATION OF BUG 1 FIX:
    /// In C# records, positional parameter attributes decorated with the "property:" target specifier
    /// are bound directly to the generated properties.
    /// Consequently, standard ASP.NET Core property-based model validation respects these constraints.
    /// </summary>
    [Fact]
    public void AddToCartRequest_PropertiesHaveValidationAttributes_WithPropertyTarget()
    {
        var sizeProp = typeof(AddToCartRequest).GetProperty(nameof(AddToCartRequest.Size));
        var qtyProp = typeof(AddToCartRequest).GetProperty(nameof(AddToCartRequest.Quantity));
        var productIdProp = typeof(AddToCartRequest).GetProperty(nameof(AddToCartRequest.ProductId));

        Assert.NotNull(sizeProp);
        Assert.NotNull(qtyProp);
        Assert.NotNull(productIdProp);

        var sizePropRangeAttrs = sizeProp.GetCustomAttributes(typeof(RangeAttribute), true);
        var qtyPropRangeAttrs = qtyProp.GetCustomAttributes(typeof(RangeAttribute), true);
        var productIdMongoAttrs = productIdProp.GetCustomAttributes(typeof(MongoObjectIdAttribute), true);
        var productIdRegexAttrs = productIdProp.GetCustomAttributes(typeof(RegularExpressionAttribute), true);

        Assert.NotEmpty(sizePropRangeAttrs);
        Assert.NotEmpty(qtyPropRangeAttrs);
        Assert.NotEmpty(productIdMongoAttrs);
        Assert.NotEmpty(productIdRegexAttrs);
    }

    [Fact]
    public void UpdateCartItemRequest_PropertyHasValidationAttribute_WithPropertyTarget()
    {
        var qtyProp = typeof(UpdateCartItemRequest).GetProperty(nameof(UpdateCartItemRequest.Quantity));
        Assert.NotNull(qtyProp);

        var rangeAttrs = qtyProp.GetCustomAttributes(typeof(RangeAttribute), true);
        Assert.NotEmpty(rangeAttrs);
    }

    [Fact]
    public void AddToCartRequest_ValidatorTryValidateObject_ValidatesPropertiesCorrectly()
    {
        var invalidReq = new AddToCartRequest(NonHex24Chars, 25, 0);
        var validationResults = new List<ValidationResult>();
        var ctx = new ValidationContext(invalidReq);

        bool isValid = Validator.TryValidateObject(invalidReq, ctx, validationResults, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(validationResults, r => r.MemberNames.Contains(nameof(AddToCartRequest.ProductId)));
        Assert.Contains(validationResults, r => r.MemberNames.Contains(nameof(AddToCartRequest.Size)));
        Assert.Contains(validationResults, r => r.MemberNames.Contains(nameof(AddToCartRequest.Quantity)));
    }

    #endregion

    #region 2. CartsController Input Validation Tests (Size & Quantity)

    [Theory]
    [InlineData(29)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(-100)]
    public async Task AddItem_SizeLessThan30_ReturnsBadRequest(int size)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var request = new AddToCartRequest(ValidObjectId, size, 1);
        var actionResult = await controller.AddItem(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(56)]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(999)]
    public async Task AddItem_SizeGreaterThan55_ReturnsBadRequest(int size)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var request = new AddToCartRequest(ValidObjectId, size, 1);
        var actionResult = await controller.AddItem(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public async Task AddItem_QuantityLessThanOrEqualZero_ReturnsBadRequest(int quantity)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var request = new AddToCartRequest(ValidObjectId, 42, quantity);
        var actionResult = await controller.AddItem(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(500)]
    public async Task AddItem_QuantityGreaterThan100_ReturnsBadRequest(int quantity)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var request = new AddToCartRequest(ValidObjectId, 42, quantity);
        var actionResult = await controller.AddItem(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("12345678901234567890123")] // 23 chars
    [InlineData("1234567890123456789012345")] // 25 chars
    public async Task AddItem_MalformedProductIdLength_ReturnsBadRequest(string? productId)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var request = new AddToCartRequest(productId!, 42, 1);
        var actionResult = await controller.AddItem(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(56)]
    public async Task UpdateItemQuantity_InvalidSize_ReturnsBadRequest(int size)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var actionResult = await controller.UpdateItemQuantity(ValidObjectId, size, new UpdateCartItemRequest(5), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task UpdateItemQuantity_InvalidQuantity_ReturnsBadRequest(int quantity)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var actionResult = await controller.UpdateItemQuantity(ValidObjectId, 42, new UpdateCartItemRequest(quantity), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid_len")]
    public async Task UpdateItemQuantity_MalformedProductIdLength_ReturnsBadRequest(string? productId)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var actionResult = await controller.UpdateItemQuantity(productId!, 42, new UpdateCartItemRequest(5), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(56)]
    public async Task RemoveItem_InvalidSize_ReturnsBadRequest(int size)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var actionResult = await controller.RemoveItem(ValidObjectId, size, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid_len")]
    public async Task RemoveItem_MalformedProductIdLength_ReturnsBadRequest(string? productId)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var actionResult = await controller.RemoveItem(productId!, 42, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    #endregion

    #region 3. Empirical Stress-Testing: Non-Hex 24-Character ProductId (Remediation of Bug 2)

    [Fact]
    public async Task AddItem_With24NonHexCharacters_ReturnsBadRequest()
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var request = new AddToCartRequest(NonHex24Chars, 42, 1);
        var actionResult = await controller.AddItem(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task UpdateItemQuantity_With24NonHexCharacters_ReturnsBadRequest()
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var actionResult = await controller.UpdateItemQuantity(NonHex24Chars, 42, new UpdateCartItemRequest(5), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task RemoveItem_With24NonHexCharacters_ReturnsBadRequest()
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var actionResult = await controller.RemoveItem(NonHex24Chars, 42, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
    }

    [Theory]
    [InlineData(NonHexWithInvalidChar)]
    [InlineData("12345678901234567890123g")]
    public async Task Endpoints_WithInvalidHexCharacter_ReturnsBadRequest(string nonHexId)
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("user1", "Customer");
        var controller = CreateController(service, user);

        var addResult = await controller.AddItem(new AddToCartRequest(nonHexId, 42, 1), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(addResult.Result);

        var updateResult = await controller.UpdateItemQuantity(nonHexId, 42, new UpdateCartItemRequest(5), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(updateResult.Result);

        var removeResult = await controller.RemoveItem(nonHexId, 42, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(removeResult.Result);
    }

    #endregion

    #region 4. RBAC (Role-Based Access Control) Tests

    /// <summary>
    /// Verifies CartsController is annotated with [Authorize(Roles = "Customer,Admin")].
    /// </summary>
    [Fact]
    public void CartsController_HasAuthorizeAttribute_WithCustomerAndAdminRoles()
    {
        var authAttr = typeof(CartsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authAttr);
        Assert.Equal("Customer,Admin", authAttr.Roles);
    }

    /// <summary>
    /// Tests ASP.NET Core Authorization Policy evaluation for roles against "Customer,Admin".
    /// Customer & Admin -> Pass
    /// Driver, LogisticsStaff, Guest -> Fail (403 Forbidden)
    /// </summary>
    [Theory]
    [InlineData("Customer", true)]
    [InlineData("Admin", true)]
    [InlineData("Driver", false)]
    [InlineData("LogisticsStaff", false)]
    [InlineData("Guest", false)]
    [InlineData("WarehouseStaff", false)]
    public async Task AuthorizationService_EvaluatesRolesAccurately(string role, bool expectedSuccess)
    {
        var services = new ServiceCollection();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("CartAccessPolicy", policy =>
            {
                policy.RequireRole("Customer", "Admin");
            });
        });
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var authService = sp.GetRequiredService<IAuthorizationService>();

        var user = CreateUserPrincipal("test_user_id", role);
        var result = await authService.AuthorizeAsync(user, "CartAccessPolicy");

        Assert.Equal(expectedSuccess, result.Succeeded);
    }

    [Fact]
    public async Task AuthorizationService_AnonymousUser_FailsAuthorization()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("CartAccessPolicy", policy =>
            {
                policy.RequireRole("Customer", "Admin");
            });
        });
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var authService = sp.GetRequiredService<IAuthorizationService>();

        var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity()); // IsAuthenticated = false
        var result = await authService.AuthorizeAsync(anonymousUser, "CartAccessPolicy");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CartsController_AnonymousOrMissingUserId_ReturnsUnauthorized401()
    {
        var service = new FakeCartService();
        var controller = CreateController(service, new ClaimsPrincipal(new ClaimsIdentity()));

        var getResult = await controller.GetMyCart(CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(getResult.Result);

        var addResult = await controller.AddItem(new AddToCartRequest(ValidObjectId, 42, 1), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(addResult.Result);

        var updateResult = await controller.UpdateItemQuantity(ValidObjectId, 42, new UpdateCartItemRequest(2), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(updateResult.Result);

        var removeResult = await controller.RemoveItem(ValidObjectId, 42, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(removeResult.Result);

        var clearResult = await controller.ClearCart(CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(clearResult.Result);
    }

    [Fact]
    public async Task CartsController_CustomerRole_AllowsCartOperations200OK()
    {
        var service = new FakeCartService();
        var user = CreateUserPrincipal("customer_user_123", "Customer");
        var controller = CreateController(service, user);

        // 1. Get empty cart -> 200 OK
        var getResult = await controller.GetMyCart(CancellationToken.None);
        var okGet = Assert.IsType<OkObjectResult>(getResult.Result);
        var cartDto = Assert.IsType<CartDto>(okGet.Value);
        Assert.Equal("customer_user_123", cartDto.UserId);
        Assert.Empty(cartDto.Items);

        // 2. Add item -> 200 OK
        var addResult = await controller.AddItem(new AddToCartRequest(ValidObjectId, 42, 2), CancellationToken.None);
        var okAdd = Assert.IsType<OkObjectResult>(addResult.Result);
        var addedCart = Assert.IsType<CartDto>(okAdd.Value);
        Assert.Single(addedCart.Items);
        Assert.Equal(2, addedCart.Items[0].Quantity);

        // 3. Update quantity -> 200 OK
        var updateResult = await controller.UpdateItemQuantity(ValidObjectId, 42, new UpdateCartItemRequest(5), CancellationToken.None);
        var okUpdate = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedCart = Assert.IsType<CartDto>(okUpdate.Value);
        Assert.Equal(5, updatedCart.Items[0].Quantity);

        // 4. Remove item -> 200 OK
        var removeResult = await controller.RemoveItem(ValidObjectId, 42, CancellationToken.None);
        var okRemove = Assert.IsType<OkObjectResult>(removeResult.Result);
        var removedCart = Assert.IsType<CartDto>(okRemove.Value);
        Assert.Empty(removedCart.Items);

        // 5. Clear cart -> 200 OK
        var clearResult = await controller.ClearCart(CancellationToken.None);
        var okClear = Assert.IsType<OkObjectResult>(clearResult.Result);
        var clearedCart = Assert.IsType<CartDto>(okClear.Value);
        Assert.Empty(clearedCart.Items);
    }

    [Fact]
    public async Task CartsController_AdminRole_AllowsCartOperations200OK()
    {
        var service = new FakeCartService();
        var admin = CreateUserPrincipal("admin_user_999", "Admin");
        var controller = CreateController(service, admin);

        // Admin can access my-cart -> 200 OK
        var getResult = await controller.GetMyCart(CancellationToken.None);
        var okGet = Assert.IsType<OkObjectResult>(getResult.Result);
        var cartDto = Assert.IsType<CartDto>(okGet.Value);
        Assert.Equal("admin_user_999", cartDto.UserId);

        // Admin can add item -> 200 OK
        var addResult = await controller.AddItem(new AddToCartRequest(ValidObjectId, 40, 1), CancellationToken.None);
        var okAdd = Assert.IsType<OkObjectResult>(addResult.Result);
        var addedCart = Assert.IsType<CartDto>(okAdd.Value);
        Assert.Single(addedCart.Items);
    }

    #endregion
}
