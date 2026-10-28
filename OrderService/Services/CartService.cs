using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OrderService.Data;
using OrderService.Dtos;
using OrderService.Models;

namespace OrderService.Services;

public class CartService : ICartService
{
    private readonly IMongoCollection<Cart> _cartsCollection;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CartService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CartService(
        IMongoDatabase database,
        IHttpClientFactory httpClientFactory,
        ILogger<CartService> logger)
    {
        _cartsCollection = database.GetCollection<Cart>(OrderDbInitializer.CartsCollectionName);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Cart> GetOrCreateCartAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId không được để trống.", nameof(userId));

        // 1. Fast path: Kiểm tra giỏ hàng đã có trong DB hay chưa (không tốn chi phí write/lock)
        var existingCart = await _cartsCollection.Find(c => c.UserId == userId).FirstOrDefaultAsync(ct);
        if (existingCart != null)
        {
            return existingCart;
        }

        // 2. Slow path: Khởi tạo giỏ hàng mới nguyên tử bằng FindOneAndUpdate với IsUpsert = true
        var filter = Builders<Cart>.Filter.Eq(c => c.UserId, userId);
        var now = DateTime.UtcNow;

        var update = Builders<Cart>.Update
            .SetOnInsert(c => c.UserId, userId)
            .SetOnInsert(c => c.CreatedAt, now)
            .SetOnInsert(c => c.UpdatedAt, now)
            .SetOnInsert(c => c.Items, new List<CartItem>());

        var options = new FindOneAndUpdateOptions<Cart>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };

        try
        {
            var cart = await _cartsCollection.FindOneAndUpdateAsync(filter, update, options, ct);
            _logger.LogInformation("Đã khởi tạo giỏ hàng mới cho người dùng '{UserId}'.", userId);
            return cart ?? (await _cartsCollection.Find(c => c.UserId == userId).FirstOrDefaultAsync(ct))
                   ?? new Cart { UserId = userId, CreatedAt = now, UpdatedAt = now };
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Tránh race condition khi 2 request cùng khởi tạo giỏ hàng rỗng ở millisecond đầu tiên
            return await _cartsCollection.Find(c => c.UserId == userId).FirstOrDefaultAsync(ct)
                   ?? throw new InvalidOperationException($"Không thể truy xuất giỏ hàng của user '{userId}' sau lỗi trùng lặp key.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Cart> AddItemToCartAsync(string userId, AddToCartRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId không được để trống.", nameof(userId));

        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.ProductId))
            throw new ArgumentException("ProductId không được để trống.", nameof(request.ProductId));

        if (request.Size < 30 || request.Size > 55)
            throw new ArgumentOutOfRangeException(nameof(request.Size), "Size giày Adidas phải nằm trong khoảng từ 30 đến 55.");

        if (request.Quantity <= 0 || request.Quantity > 100)
            throw new ArgumentOutOfRangeException(nameof(request.Quantity), "Số lượng thêm vào giỏ phải từ 1 đến 100.");

        // Đảm bảo giỏ hàng đã tồn tại
        await GetOrCreateCartAsync(userId, ct);

        // BƯỚC 1: Thử tăng số lượng nguyên tử nếu cặp (ProductId, Size) đã có sẵn trong giỏ
        var filterExisting = Builders<Cart>.Filter.And(
            Builders<Cart>.Filter.Eq(c => c.UserId, userId),
            Builders<Cart>.Filter.ElemMatch(c => c.Items, i => i.ProductId == request.ProductId && i.Size == request.Size)
        );

        var updateInc = Builders<Cart>.Update
            .Inc("items.$.quantity", request.Quantity)
            .Set(c => c.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Cart>
        {
            ReturnDocument = ReturnDocument.After
        };

        var cartWithIncrementedItem = await _cartsCollection.FindOneAndUpdateAsync(filterExisting, updateInc, options, ct);
        if (cartWithIncrementedItem != null)
        {
            _logger.LogInformation("Đã tăng số lượng sản phẩm '{ProductId}' size {Size} thêm {Quantity} trong giỏ của user '{UserId}'.",
                request.ProductId, request.Size, request.Quantity, userId);
            return cartWithIncrementedItem;
        }

        // BƯỚC 2: Mục chưa có trong giỏ -> Tra cứu thông tin sản phẩm từ CatalogService
        var (productName, sku, unitPrice, thumbnailUrl) = await ResolveProductSnapshotAsync(request, ct);

        var newItem = new CartItem
        {
            ProductId = request.ProductId,
            ProductName = productName,
            ProductSku = sku,
            Size = request.Size,
            Quantity = request.Quantity,
            UnitPrice = unitPrice,
            ThumbnailUrl = thumbnailUrl
        };

        // BƯỚC 3: Push nguyên tử mục mới vào mảng Items, kèm điều kiện chống trùng lặp song song
        var filterNotExists = Builders<Cart>.Filter.And(
            Builders<Cart>.Filter.Eq(c => c.UserId, userId),
            Builders<Cart>.Filter.Not(
                Builders<Cart>.Filter.ElemMatch(c => c.Items, i => i.ProductId == request.ProductId && i.Size == request.Size)
            )
        );

        var updatePush = Builders<Cart>.Update
            .Push(c => c.Items, newItem)
            .Set(c => c.UpdatedAt, DateTime.UtcNow);

        var cartWithPushedItem = await _cartsCollection.FindOneAndUpdateAsync(filterNotExists, updatePush, options, ct);
        if (cartWithPushedItem != null)
        {
            _logger.LogInformation("Đã thêm mới sản phẩm '{ProductId}' size {Size} (Qty: {Quantity}) vào giỏ của user '{UserId}'.",
                request.ProductId, request.Size, request.Quantity, userId);
            return cartWithPushedItem;
        }

        // BƯỚC 4: Trường hợp biên: 1 request song song khác đã push đúng size này vào trước tích tắc.
        // Thực hiện cộng dồn số lượng bằng lệnh $inc fallback.
        var fallbackInc = await _cartsCollection.FindOneAndUpdateAsync(filterExisting, updateInc, options, ct);
        if (fallbackInc != null)
        {
            return fallbackInc;
        }

        return await GetOrCreateCartAsync(userId, ct);
    }

    /// <inheritdoc />
    public async Task<Cart?> UpdateItemQuantityAsync(string userId, string productId, int size, int quantity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId không được để trống.", nameof(userId));

        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("ProductId không được để trống.", nameof(productId));

        if (size < 30 || size > 55)
            throw new ArgumentOutOfRangeException(nameof(size), "Size giày phải nằm trong khoảng từ 30 đến 55.");

        // Nếu số lượng <= 0: tự động chuyển thành thao tác xóa
        if (quantity <= 0)
        {
            return await RemoveItemFromCartAsync(userId, productId, size, ct);
        }

        if (quantity > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Số lượng sản phẩm trong giỏ không được vượt quá 100.");
        }

        var filter = Builders<Cart>.Filter.And(
            Builders<Cart>.Filter.Eq(c => c.UserId, userId),
            Builders<Cart>.Filter.ElemMatch(c => c.Items, i => i.ProductId == productId && i.Size == size)
        );

        var update = Builders<Cart>.Update
            .Set("items.$.quantity", quantity)
            .Set(c => c.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Cart>
        {
            ReturnDocument = ReturnDocument.After
        };

        var updatedCart = await _cartsCollection.FindOneAndUpdateAsync(filter, update, options, ct);
        if (updatedCart != null)
        {
            _logger.LogInformation("Đã cập nhật số lượng sản phẩm '{ProductId}' size {Size} thành {Quantity} trong giỏ của user '{UserId}'.",
                productId, size, quantity, userId);
        }

        return updatedCart;
    }

    /// <inheritdoc />
    public async Task<Cart?> RemoveItemFromCartAsync(string userId, string productId, int size, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId không được để trống.", nameof(userId));

        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("ProductId không được để trống.", nameof(productId));

        // Kiểm tra xem giỏ hàng và sản phẩm tương ứng có tồn tại hay không
        var existingCart = await _cartsCollection.Find(c => c.UserId == userId).FirstOrDefaultAsync(ct);
        if (existingCart == null || existingCart.Items == null || !existingCart.Items.Any(i => i.ProductId == productId && i.Size == size))
        {
            return null;
        }

        var filter = Builders<Cart>.Filter.Eq(c => c.UserId, userId);

        var pullFilter = Builders<CartItem>.Filter.And(
            Builders<CartItem>.Filter.Eq(i => i.ProductId, productId),
            Builders<CartItem>.Filter.Eq(i => i.Size, size)
        );

        var update = Builders<Cart>.Update
            .PullFilter(c => c.Items, pullFilter)
            .Set(c => c.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Cart>
        {
            ReturnDocument = ReturnDocument.After
        };

        var updatedCart = await _cartsCollection.FindOneAndUpdateAsync(filter, update, options, ct);
        if (updatedCart != null)
        {
            _logger.LogInformation("Đã xóa sản phẩm '{ProductId}' size {Size} khỏi giỏ hàng của user '{UserId}'.",
                productId, size, userId);
        }

        return updatedCart;
    }

    /// <inheritdoc />
    public async Task<Cart> ClearCartAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId không được để trống.", nameof(userId));

        var filter = Builders<Cart>.Filter.Eq(c => c.UserId, userId);

        var update = Builders<Cart>.Update
            .Set(c => c.Items, new List<CartItem>())
            .Set(c => c.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Cart>
        {
            ReturnDocument = ReturnDocument.After
        };

        var updatedCart = await _cartsCollection.FindOneAndUpdateAsync(filter, update, options, ct);
        if (updatedCart == null)
        {
            return await GetOrCreateCartAsync(userId, ct);
        }

        _logger.LogInformation("Đã làm trống giỏ hàng của user '{UserId}'.", userId);
        return updatedCart;
    }

    /// <summary>
    /// Tra cứu chi tiết sản phẩm từ CatalogService để snapshot vào giỏ hàng.
    /// Có cơ chế phòng vệ (Graceful Fallback) khi CatalogService đang offline hoặc rớt mạng.
    /// </summary>
    private async Task<(string Name, string? Sku, decimal UnitPrice, string ThumbnailUrl)> ResolveProductSnapshotAsync(
        AddToCartRequest request, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("CatalogClient");
            var response = await client.GetAsync($"api/catalog/products/{Uri.EscapeDataString(request.ProductId)}", ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new KeyNotFoundException($"Không tìm thấy sản phẩm '{request.ProductId}' trong hệ thống Catalog.");
            }

            if (response.IsSuccessStatusCode)
            {
                var product = await response.Content.ReadFromJsonAsync<CatalogProductInfo>(JsonOptions, ct);
                if (product != null)
                {
                    // Kiểm tra size có được hỗ trợ bởi mẫu giày này không
                    if (product.Sizes != null && product.Sizes.Count > 0 && !product.Sizes.Any(s => s.Size == request.Size))
                    {
                        throw new ArgumentException($"Mẫu giày '{product.Name}' hiện không có size {request.Size}.");
                    }

                    var price = product.SalePrice ?? product.Price;
                    var sku = !string.IsNullOrWhiteSpace(product.Category)
                        ? $"{product.Category.ToUpperInvariant()}-{request.Size}"
                        : $"ADI-{request.Size}";

                    var name = !string.IsNullOrWhiteSpace(product.Name) ? product.Name : $"Adidas Shoes ({request.ProductId})";
                    var thumbnail = product.ThumbnailUrl ?? string.Empty;

                    return (name, sku, price, thumbnail);
                }
            }
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể kết nối CatalogService khi tải thông tin sản phẩm '{ProductId}'. Sử dụng dữ liệu dự phòng.", request.ProductId);
        }

        // Dự phòng từ thông tin request gửi lên hoặc giá trị mặc định
        var fallbackName = !string.IsNullOrWhiteSpace(request.ProductName)
            ? request.ProductName
            : $"Giày thể thao Adidas ({request.ProductId})";

        var fallbackSku = request.ProductSku ?? $"ADI-{request.Size}";
        var fallbackPrice = request.UnitPrice ?? 0m;
        var fallbackThumbnail = request.ThumbnailUrl ?? string.Empty;

        return (fallbackName, fallbackSku, fallbackPrice, fallbackThumbnail);
    }
}

/// <summary>
/// Model DTO ánh xạ thông tin sản phẩm từ CatalogService
/// </summary>
internal record CatalogProductInfo(
    string? Id,
    string? Name,
    string? Category,
    decimal Price,
    decimal? SalePrice,
    string? Color,
    string? ThumbnailUrl,
    List<CatalogProductSizeInfo>? Sizes);

/// <summary>
/// Model DTO ánh xạ kích cỡ tồn kho từ CatalogService
/// </summary>
internal record CatalogProductSizeInfo(
    int Size,
    int Quantity,
    int ReservedQuantity);
