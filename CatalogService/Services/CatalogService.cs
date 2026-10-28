using CatalogService.Dtos;
using CatalogService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CatalogService.Services;

public interface ICatalogService
{
    Task<PagedResult<ShoeProduct>> GetProductsAsync(ProductFilterQuery query);
    Task<ShoeProduct?> GetProductByIdAsync(string id, bool includeInactive = false);
    Task<ShoeProduct> CreateProductAsync(CreateProductRequest req);
    Task<ShoeProduct?> UpdateProductAsync(string id, UpdateProductRequest req);
    Task<bool> DeleteProductAsync(string id, bool hardDelete = false);
    Task<bool> RestoreProductAsync(string id);
    Task<ShoeProduct?> AddReviewAsync(string id, string userId, string fullName, AddReviewRequest req);
    Task<CatalogFiltersResponse> GetFiltersAsync();

    // Saga / Internal Stock Control APIs
    Task<StockOperationResult> ReserveStockAsync(ReserveStockRequest req);
    Task<StockOperationResult> ConfirmStockAsync(ConfirmStockRequest req);
    Task<StockOperationResult> ReleaseStockAsync(ReleaseStockRequest req, bool isExpiredByWorker = false);
    Task<StockReservation?> GetReservationByOrderIdAsync(string orderId);
    Task<List<StockReservation>> GetReservationsAsync(string? status = null, int limit = 50);
    Task<int> ReleaseExpiredReservationsAsync(CancellationToken cancellationToken = default);
}

public class ProductCatalogService : ICatalogService
{
    public const string ProductsCollectionName = "Products";
    public const string StockReservationsCollectionName = "StockReservations";

    private readonly IMongoCollection<ShoeProduct> _products;
    private readonly IMongoCollection<StockReservation> _reservations;
    private readonly ILogger<ProductCatalogService> _logger;

    public ProductCatalogService(IMongoDatabase database, ILogger<ProductCatalogService> logger)
    {
        _products = database.GetCollection<ShoeProduct>(ProductsCollectionName);
        _reservations = database.GetCollection<StockReservation>(StockReservationsCollectionName);
        _logger = logger;
    }

    public async Task<PagedResult<ShoeProduct>> GetProductsAsync(ProductFilterQuery query)
    {
        var filterBuilder = Builders<ShoeProduct>.Filter;

        // 0. Lọc trạng thái Active (Mặc định chỉ lấy sản phẩm đang kinh doanh)
        var filter = query.IsActive.HasValue
            ? filterBuilder.Eq(p => p.IsActive, query.IsActive.Value)
            : filterBuilder.Eq(p => p.IsActive, true);

        // 1. Tìm kiếm theo từ khóa (Tên hoặc Màu hoặc Mô tả)
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            var keywordFilter = filterBuilder.Regex(p => p.Name, new BsonRegularExpression(keyword, "i")) |
                                filterBuilder.Regex(p => p.Color, new BsonRegularExpression(keyword, "i")) |
                                filterBuilder.Regex(p => p.Category, new BsonRegularExpression(keyword, "i"));
            filter &= keywordFilter;
        }

        // 2. Lọc theo Dòng giày (Category)
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            filter &= filterBuilder.Eq(p => p.Category, query.Category.Trim());
        }

        // 3. Lọc theo Giới tính (Gender)
        if (!string.IsNullOrWhiteSpace(query.Gender))
        {
            filter &= filterBuilder.Eq(p => p.Gender, query.Gender.Trim());
        }

        // 4. Lọc theo Màu sắc (Color)
        if (!string.IsNullOrWhiteSpace(query.Color))
        {
            filter &= filterBuilder.Regex(p => p.Color, new BsonRegularExpression(query.Color.Trim(), "i"));
        }

        // 5. Lọc theo Size chân (chỉ lấy các đôi có size này và số lượng tồn > 0)
        if (query.Size.HasValue)
        {
            filter &= filterBuilder.ElemMatch(p => p.Sizes, s => s.Size == query.Size.Value && s.Quantity > 0);
        }

        // 6. Lọc theo Khoảng giá (tính theo SalePrice nếu có, ngược lại tính theo Price)
        if (query.MinPrice.HasValue)
        {
            var min = query.MinPrice.Value;
            var priceFilter = (filterBuilder.Ne(p => p.SalePrice, null) & filterBuilder.Gte(p => p.SalePrice, min)) |
                              (filterBuilder.Eq(p => p.SalePrice, null) & filterBuilder.Gte(p => p.Price, min));
            filter &= priceFilter;
        }
        if (query.MaxPrice.HasValue)
        {
            var max = query.MaxPrice.Value;
            var priceFilter = (filterBuilder.Ne(p => p.SalePrice, null) & filterBuilder.Lte(p => p.SalePrice, max)) |
                              (filterBuilder.Eq(p => p.SalePrice, null) & filterBuilder.Lte(p => p.Price, max));
            filter &= priceFilter;
        }

        // 7. Lọc chỉ lấy sản phẩm Đang giảm giá (OnSale = true)
        if (query.OnSale == true)
        {
            filter &= filterBuilder.Where(p => p.SalePrice != null && p.SalePrice < p.Price);
        }

        // 8. Lọc theo Tag (Best Seller, New Arrival, Hot Sale...)
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            filter &= filterBuilder.AnyEq(p => p.Tags, query.Tag.Trim());
        }

        // Sắp xếp (Sorting)
        var sortBuilder = Builders<ShoeProduct>.Sort;
        SortDefinition<ShoeProduct> sort = query.SortBy?.ToLowerInvariant() switch
        {
            "price_asc" => sortBuilder.Ascending(p => p.Price),
            "price_desc" => sortBuilder.Descending(p => p.Price),
            "rating" => sortBuilder.Descending(p => p.AverageRating),
            "popular" => sortBuilder.Descending(p => p.ReviewCount),
            _ => sortBuilder.Descending(p => p.CreatedAt) // default newest
        };

        var totalCount = await _products.CountDocumentsAsync(filter);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        var items = await _products.Find(filter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        return new PagedResult<ShoeProduct>(items, totalCount, page, pageSize, totalPages);
    }

    public async Task<ShoeProduct?> GetProductByIdAsync(string id, bool includeInactive = false)
    {
        if (!ObjectId.TryParse(id, out _))
            return null;

        if (includeInactive)
        {
            return await _products.Find(p => p.Id == id).FirstOrDefaultAsync();
        }
        return await _products.Find(p => p.Id == id && p.IsActive).FirstOrDefaultAsync();
    }

    public async Task<ShoeProduct> CreateProductAsync(CreateProductRequest req)
    {
        var product = new ShoeProduct
        {
            Name = req.Name.Trim(),
            Category = req.Category.Trim(),
            Gender = string.IsNullOrWhiteSpace(req.Gender) ? "Unisex" : req.Gender.Trim(),
            Description = req.Description.Trim(),
            Price = req.Price,
            SalePrice = req.SalePrice,
            Color = req.Color.Trim(),
            Sizes = req.Sizes?.Select(s => new SizeStock { Size = s.Size, Quantity = s.Quantity, ReservedQuantity = 0 }).ToList() ?? new(),
            ThumbnailUrl = req.ThumbnailUrl.Trim(),
            ImageUrls = req.ImageUrls ?? new(),
            Tags = req.Tags ?? new(),
            CreatedAt = DateTime.UtcNow,
            IsActive = req.IsActive
        };

        await _products.InsertOneAsync(product);
        return product;
    }

    public async Task<ShoeProduct?> UpdateProductAsync(string id, UpdateProductRequest req)
    {
        if (!ObjectId.TryParse(id, out _))
            return null;

        var existing = await _products.Find(p => p.Id == id).FirstOrDefaultAsync();
        if (existing == null)
            return null;

        // Bảo toàn số lượng đang tạm giữ (ReservedQuantity) của từng size khi Admin cập nhật thông tin sản phẩm
        var reservedBySize = existing.Sizes.ToDictionary(s => s.Size, s => s.ReservedQuantity);
        var updatedSizes = req.Sizes?.Select(s => new SizeStock
        {
            Size = s.Size,
            Quantity = s.Quantity,
            ReservedQuantity = reservedBySize.GetValueOrDefault(s.Size, 0)
        }).ToList() ?? new();

        var update = Builders<ShoeProduct>.Update
            .Set(p => p.Name, req.Name.Trim())
            .Set(p => p.Category, req.Category.Trim())
            .Set(p => p.Gender, string.IsNullOrWhiteSpace(req.Gender) ? "Unisex" : req.Gender.Trim())
            .Set(p => p.Description, req.Description.Trim())
            .Set(p => p.Price, req.Price)
            .Set(p => p.SalePrice, req.SalePrice)
            .Set(p => p.Color, req.Color.Trim())
            .Set(p => p.Sizes, updatedSizes)
            .Set(p => p.ThumbnailUrl, req.ThumbnailUrl.Trim())
            .Set(p => p.ImageUrls, req.ImageUrls ?? new())
            .Set(p => p.Tags, req.Tags ?? new())
            .Set(p => p.IsActive, req.IsActive)
            .Set(p => p.UpdatedAt, DateTime.UtcNow);

        return await _products.FindOneAndUpdateAsync(
            p => p.Id == id,
            update,
            new FindOneAndUpdateOptions<ShoeProduct> { ReturnDocument = ReturnDocument.After });
    }

    public async Task<bool> DeleteProductAsync(string id, bool hardDelete = false)
    {
        if (!ObjectId.TryParse(id, out _))
            return false;

        // Kiểm tra xem sản phẩm có tồn tại trong DB không
        var product = await _products.Find(p => p.Id == id).FirstOrDefaultAsync();
        if (product == null)
            return false;

        if (hardDelete)
        {
            // Xóa cứng: xóa vĩnh viễn khỏi MongoDB Database
            var deleteResult = await _products.DeleteOneAsync(p => p.Id == id);
            return deleteResult.DeletedCount > 0;
        }

        // Nếu đã bị xóa mềm trước đó rồi, coi như thành công (idempotent)
        if (!product.IsActive)
            return true;

        // Đánh dấu IsActive = false và cập nhật thời gian
        var result = await _products.UpdateOneAsync(
            p => p.Id == id,
            Builders<ShoeProduct>.Update
                .Set(p => p.IsActive, false)
                .Set(p => p.UpdatedAt, DateTime.UtcNow));

        return result.MatchedCount > 0;
    }

    public async Task<bool> RestoreProductAsync(string id)
    {
        if (!ObjectId.TryParse(id, out _))
            return false;

        var product = await _products.Find(p => p.Id == id).FirstOrDefaultAsync();
        if (product == null)
            return false;

        if (product.IsActive)
            return true; // Đã active sẵn rồi

        var result = await _products.UpdateOneAsync(
            p => p.Id == id,
            Builders<ShoeProduct>.Update
                .Set(p => p.IsActive, true)
                .Set(p => p.UpdatedAt, DateTime.UtcNow));

        return result.MatchedCount > 0;
    }

    public async Task<ShoeProduct?> AddReviewAsync(string id, string userId, string fullName, AddReviewRequest req)
    {
        if (!ObjectId.TryParse(id, out _))
            return null;

        var product = await _products.Find(p => p.Id == id && p.IsActive).FirstOrDefaultAsync();
        if (product == null) return null;

        var newReview = new ShoeReview
        {
            UserId = userId,
            FullName = fullName,
            Rating = Math.Clamp(req.Rating, 1, 5),
            Comment = req.Comment.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        var allReviews = product.Reviews.Concat(new[] { newReview }).ToList();
        var avgRating = Math.Round(allReviews.Average(r => r.Rating), 1);
        var reviewCount = allReviews.Count;

        var update = Builders<ShoeProduct>.Update
            .Push(p => p.Reviews, newReview)
            .Set(p => p.AverageRating, avgRating)
            .Set(p => p.ReviewCount, reviewCount);

        return await _products.FindOneAndUpdateAsync(
            p => p.Id == id,
            update,
            new FindOneAndUpdateOptions<ShoeProduct> { ReturnDocument = ReturnDocument.After });
    }

    public async Task<CatalogFiltersResponse> GetFiltersAsync()
    {
        var activeProducts = await _products.Find(p => p.IsActive).ToListAsync();

        var categories = activeProducts.Select(p => p.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList();
        var genders = activeProducts.Select(p => p.Gender).Where(g => !string.IsNullOrEmpty(g)).Distinct().OrderBy(g => g).ToList();
        var colors = activeProducts.Select(p => p.Color).Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList();
        var sizes = activeProducts.SelectMany(p => p.Sizes.Where(s => s.Quantity > 0).Select(s => s.Size)).Distinct().OrderBy(s => s).ToList();
        var tags = activeProducts.SelectMany(p => p.Tags).Distinct().OrderBy(t => t).ToList();

        decimal minPrice = activeProducts.Any() ? activeProducts.Min(p => p.SalePrice ?? p.Price) : 0;
        decimal maxPrice = activeProducts.Any() ? activeProducts.Max(p => p.Price) : 0;

        return new CatalogFiltersResponse(
            categories,
            genders,
            colors,
            sizes,
            tags,
            new PriceRangeDto(minPrice, maxPrice));
    }

    // ===================== SAGA / ATOMIC STOCK RESERVATION ENGINE =====================

    /// <summary>
    /// 1. Tạm khóa tồn kho nguyên tử (Atomic Stock Reservation) theo OrderId + Danh sách (ProductId, Size, Quantity).
    /// - Chống bán lố (Overselling F-01): Dùng FindOneAndUpdateAsync với điều kiện ElemMatch(s.Size == size && s.Quantity >= qty).
    /// - Tính Lũy đẳng (Idempotency): Cùng 1 OrderId gọi lại sẽ trả về ngay bản ghi đã khóa mà không trừ kho lần 2.
    /// - All-or-Nothing Compensation: Nếu 1 đôi trong giỏ hết hàng, tự động hoàn trả lại các đôi vừa khóa trước đó.
    /// </summary>
    public async Task<StockOperationResult> ReserveStockAsync(ReserveStockRequest req)
    {
        var orderId = req.OrderId.Trim();

        // Chốt chặn 1: Kiểm tra tính Lũy đẳng (Idempotency) theo OrderId
        var existingReservation = await _reservations.Find(r => r.OrderId == orderId).FirstOrDefaultAsync();
        if (existingReservation != null)
        {
            if (existingReservation.Status == StockReservationStatus.Reserved ||
                existingReservation.Status == StockReservationStatus.Confirmed)
            {
                return new StockOperationResult(
                    Success: true,
                    Status: existingReservation.Status,
                    Message: $"Phiên giữ hàng cho đơn '{orderId}' đã tồn tại (Idempotent Replay - Không trừ kho lần 2).",
                    IsIdempotentReplay: true,
                    Reservation: existingReservation);
            }

            return new StockOperationResult(
                Success: false,
                Status: existingReservation.Status,
                Message: $"Phiên giữ hàng của đơn '{orderId}' đã kết thúc ở trạng thái '{existingReservation.Status}'. Vui lòng tạo mã đơn mới.",
                IsIdempotentReplay: true,
                Reservation: existingReservation);
        }

        // Gộp các dòng trùng (ProductId + Size) để thực thi nguyên tử trên từng cặp (ProductId, Size)
        var normalizedItems = req.Items
            .GroupBy(i => (ProductId: i.ProductId.Trim(), i.Size))
            .Select(g => new ReserveStockItemRequest(g.Key.ProductId, g.Key.Size, g.Sum(x => x.Quantity)))
            .ToList();

        // Kiểm tra định dạng ObjectId trước khi tác động vào DB
        foreach (var item in normalizedItems)
        {
            if (!ObjectId.TryParse(item.ProductId, out _))
            {
                return new StockOperationResult(
                    Success: false,
                    Status: "InvalidProductId",
                    Message: $"Mã sản phẩm '{item.ProductId}' không đúng định dạng ObjectId 24 ký tự hex.",
                    IsIdempotentReplay: false,
                    FailedItems:
                    [
                        new StockFailureDetail(item.ProductId, null, item.Size, item.Quantity, 0, "INVALID_PRODUCT_ID", "ProductId không đúng định dạng ObjectId.")
                    ]);
            }
        }

        var lockedItems = new List<ReservedStockItem>();
        var now = DateTime.UtcNow;

        foreach (var item in normalizedItems)
        {
            // Bộ lọc nguyên tử: Sản phẩm phải đang Active VÀ có đúng Size đó với Quantity >= số lượng yêu cầu
            var atomicFilter = Builders<ShoeProduct>.Filter.And(
                Builders<ShoeProduct>.Filter.Eq(p => p.Id, item.ProductId),
                Builders<ShoeProduct>.Filter.Eq(p => p.IsActive, true),
                Builders<ShoeProduct>.Filter.ElemMatch(
                    p => p.Sizes,
                    s => s.Size == item.Size && s.Quantity >= item.Quantity));

            // Lệnh cập nhật nguyên tử: Giảm Quantity khả dụng và tăng ReservedQuantity trong cùng 1 thao tác khóa dòng của MongoDB
            var atomicUpdate = Builders<ShoeProduct>.Update
                .Inc("sizes.$.quantity", -item.Quantity)
                .Inc("sizes.$.reservedQuantity", item.Quantity)
                .Set(p => p.UpdatedAt, now);

            var updatedProduct = await _products.FindOneAndUpdateAsync(
                atomicFilter,
                atomicUpdate,
                new FindOneAndUpdateOptions<ShoeProduct> { ReturnDocument = ReturnDocument.After });

            if (updatedProduct == null)
            {
                // Có ít nhất 1 món không đủ tồn kho -> Thực hiện Compensation: Nhả lại ngay lập tức toàn bộ các món đã khóa trước đó
                if (lockedItems.Count > 0)
                {
                    await RollbackLockedItemsAsync(lockedItems);
                }

                var failureDetail = await DiagnoseStockFailureAsync(item);
                _logger.LogWarning(
                    "Khóa kho thất bại cho OrderId={OrderId} tại ProductId={ProductId}, Size={Size}, Requested={Qty}. Lý do: {Reason}",
                    orderId, item.ProductId, item.Size, item.Quantity, failureDetail.Reason);

                return new StockOperationResult(
                    Success: false,
                    Status: "OutOfStock",
                    Message: failureDetail.Reason,
                    IsIdempotentReplay: false,
                    FailedItems: [failureDetail]);
            }

            // Chụp Snapshot giá gốc từ CatalogDb để OrderService áp dụng Zero-Trust Pricing
            var unitPrice = updatedProduct.SalePrice ?? updatedProduct.Price;
            lockedItems.Add(new ReservedStockItem
            {
                ProductId = updatedProduct.Id!,
                ProductName = updatedProduct.Name,
                Category = updatedProduct.Category,
                Color = updatedProduct.Color,
                Size = item.Size,
                Quantity = item.Quantity,
                OriginalPrice = updatedProduct.Price,
                SalePrice = updatedProduct.SalePrice,
                UnitPriceSnapshot = unitPrice,
                ThumbnailUrl = updatedProduct.ThumbnailUrl
            });
        }

        var holdMinutes = Math.Clamp(req.HoldMinutes, 1, 1440);
        var reservation = new StockReservation
        {
            OrderId = orderId,
            OrderCode = string.IsNullOrWhiteSpace(req.OrderCode) ? orderId : req.OrderCode.Trim(),
            UserId = req.UserId?.Trim() ?? string.Empty,
            Items = lockedItems,
            Status = StockReservationStatus.Reserved,
            HoldMinutes = holdMinutes,
            ExpiresAt = now.AddMinutes(holdMinutes),
            CreatedAt = now
        };

        try
        {
            await _reservations.InsertOneAsync(reservation);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Trường hợp 2 request trùng OrderId bắn song song ở cùng 1 mili-giây:
            // Hoàn trả lại phần tồn kho của request đến sau và trả về bản ghi của request chiến thắng
            await RollbackLockedItemsAsync(lockedItems);
            var winner = await _reservations.Find(r => r.OrderId == orderId).FirstOrDefaultAsync();
            return new StockOperationResult(
                Success: winner != null && (winner.Status == StockReservationStatus.Reserved || winner.Status == StockReservationStatus.Confirmed),
                Status: winner?.Status ?? StockReservationStatus.Reserved,
                Message: $"Phiên giữ hàng cho đơn '{orderId}' đã được xử lý đồng thời (Idempotent Concurrent Replay).",
                IsIdempotentReplay: true,
                Reservation: winner);
        }

        _logger.LogInformation(
            "Đã khóa tồn kho thành công cho OrderId={OrderId} ({TotalQty} đôi, SubTotal={SubTotal:N0} VNĐ). Hết hạn lúc {ExpiresAt:O}.",
            orderId, reservation.TotalQuantity, reservation.SubTotalSnapshot, reservation.ExpiresAt);

        return new StockOperationResult(
            Success: true,
            Status: StockReservationStatus.Reserved,
            Message: $"Đã khóa tồn kho thành công {reservation.TotalQuantity} đôi giày trong {holdMinutes} phút.",
            IsIdempotentReplay: false,
            Reservation: reservation);
    }

    /// <summary>
    /// 2. Xác nhận xuất kho chính thức (Confirm Stock Deduction) sau khi khách thanh toán thành công hoặc đơn COD hợp lệ.
    /// Giải phóng ReservedQuantity (vì Quantity khả dụng đã được trừ từ lúc Reserve).
    /// </summary>
    public async Task<StockOperationResult> ConfirmStockAsync(ConfirmStockRequest req)
    {
        var orderId = req.OrderId.Trim();
        var existing = await _reservations.Find(r => r.OrderId == orderId).FirstOrDefaultAsync();
        if (existing == null)
        {
            return new StockOperationResult(
                Success: false,
                Status: "NotFound",
                Message: $"Không tìm thấy phiên giữ hàng cho OrderId '{orderId}'.",
                IsIdempotentReplay: false);
        }

        if (existing.Status == StockReservationStatus.Confirmed)
        {
            return new StockOperationResult(
                Success: true,
                Status: StockReservationStatus.Confirmed,
                Message: $"Tồn kho của đơn '{orderId}' đã được xác nhận xuất kho trước đó (Idempotent Replay).",
                IsIdempotentReplay: true,
                Reservation: existing);
        }

        if (existing.Status != StockReservationStatus.Reserved)
        {
            return new StockOperationResult(
                Success: false,
                Status: existing.Status,
                Message: $"Không thể xác nhận xuất kho cho đơn '{orderId}' vì phiên giữ hàng đang ở trạng thái '{existing.Status}' (đã quá hạn hoặc đã hủy).",
                IsIdempotentReplay: false,
                Reservation: existing);
        }

        var now = DateTime.UtcNow;
        var confirmed = await _reservations.FindOneAndUpdateAsync(
            r => r.OrderId == orderId && r.Status == StockReservationStatus.Reserved,
            Builders<StockReservation>.Update
                .Set(r => r.Status, StockReservationStatus.Confirmed)
                .Set(r => r.ConfirmedAt, now)
                .Set(r => r.UpdatedAt, now)
                .Set(r => r.ReleaseReason, req.Reason ?? "PaymentVerified"),
            new FindOneAndUpdateOptions<StockReservation> { ReturnDocument = ReturnDocument.After });

        if (confirmed == null)
        {
            var current = await _reservations.Find(r => r.OrderId == orderId).FirstOrDefaultAsync();
            return new StockOperationResult(
                Success: current?.Status == StockReservationStatus.Confirmed,
                Status: current?.Status ?? "Unknown",
                Message: $"Trạng thái phiên giữ hàng của đơn '{orderId}' đã thay đổi đồng thời sang '{current?.Status}'.",
                IsIdempotentReplay: true,
                Reservation: current);
        }

        // Giải phóng ReservedQuantity (Quantity thực tế đã giảm ngay từ bước Reserve)
        foreach (var item in confirmed.Items)
        {
            var filter = Builders<ShoeProduct>.Filter.And(
                Builders<ShoeProduct>.Filter.Eq(p => p.Id, item.ProductId),
                Builders<ShoeProduct>.Filter.ElemMatch(p => p.Sizes, s => s.Size == item.Size && s.ReservedQuantity >= item.Quantity));

            var update = Builders<ShoeProduct>.Update
                .Inc("sizes.$.reservedQuantity", -item.Quantity)
                .Set(p => p.UpdatedAt, now);

            await _products.UpdateOneAsync(filter, update);
        }

        _logger.LogInformation("Đã xác nhận xuất kho vĩnh viễn cho OrderId={OrderId}. Lý do: {Reason}", orderId, req.Reason);

        return new StockOperationResult(
            Success: true,
            Status: StockReservationStatus.Confirmed,
            Message: $"Đã xác nhận trừ kho chính thức cho đơn hàng '{orderId}'.",
            IsIdempotentReplay: false,
            Reservation: confirmed);
    }

    /// <summary>
    /// 3. Nhả tồn kho (Release / Compensate Stock) khi đơn hàng hủy, lỗi thanh toán, hết hạn 15 phút, hoặc hoàn hàng về kho (RTO).
    /// Đảm bảo tính Lũy đẳng (Idempotent): Gọi nhả kho 2 lần cho cùng 1 OrderId tuyệt đối không cộng thừa số lượng tồn kho.
    /// </summary>
    public async Task<StockOperationResult> ReleaseStockAsync(ReleaseStockRequest req, bool isExpiredByWorker = false)
    {
        var orderId = req.OrderId.Trim();
        var existing = await _reservations.Find(r => r.OrderId == orderId).FirstOrDefaultAsync();
        if (existing == null)
        {
            return new StockOperationResult(
                Success: false,
                Status: "NotFound",
                Message: $"Không tìm thấy phiên giữ hàng cho OrderId '{orderId}'.",
                IsIdempotentReplay: false);
        }

        // Nếu đã Released hoặc Expired trước đó -> Trả về thành công ngay (Idempotent Replay)
        if (existing.Status == StockReservationStatus.Released ||
            existing.Status == StockReservationStatus.Expired)
        {
            return new StockOperationResult(
                Success: true,
                Status: existing.Status,
                Message: $"Tồn kho của đơn '{orderId}' đã được hoàn trả về kệ trước đó (Idempotent Replay - Không cộng trùng kho).",
                IsIdempotentReplay: true,
                Reservation: existing);
        }

        var now = DateTime.UtcNow;
        var targetStatus = isExpiredByWorker ? StockReservationStatus.Expired : StockReservationStatus.Released;
        var previousStatus = existing.Status;

        // Chuyển trạng thái nguyên tử từ previousStatus (Reserved hoặc Confirmed) sang Released/Expired
        var released = await _reservations.FindOneAndUpdateAsync(
            r => r.OrderId == orderId && r.Status == previousStatus,
            Builders<StockReservation>.Update
                .Set(r => r.Status, targetStatus)
                .Set(r => r.ReleasedAt, now)
                .Set(r => r.UpdatedAt, now)
                .Set(r => r.ReleaseReason, req.Reason),
            new FindOneAndUpdateOptions<StockReservation> { ReturnDocument = ReturnDocument.After });

        if (released == null)
        {
            var current = await _reservations.Find(r => r.OrderId == orderId).FirstOrDefaultAsync();
            return new StockOperationResult(
                Success: current?.Status is StockReservationStatus.Released or StockReservationStatus.Expired,
                Status: current?.Status ?? "Unknown",
                Message: $"Phiên giữ hàng của đơn '{orderId}' đã được xử lý đồng thời sang trạng thái '{current?.Status}'.",
                IsIdempotentReplay: true,
                Reservation: current);
        }

        foreach (var item in released.Items)
        {
            if (previousStatus == StockReservationStatus.Reserved)
            {
                // Nếu đang ở trạng thái Reserved: Cộng lại Quantity khả dụng VÀ giảm ReservedQuantity
                var filter = Builders<ShoeProduct>.Filter.And(
                    Builders<ShoeProduct>.Filter.Eq(p => p.Id, item.ProductId),
                    Builders<ShoeProduct>.Filter.ElemMatch(p => p.Sizes, s => s.Size == item.Size));

                var update = Builders<ShoeProduct>.Update
                    .Inc("sizes.$.quantity", item.Quantity)
                    .Inc("sizes.$.reservedQuantity", -item.Quantity)
                    .Set(p => p.UpdatedAt, now);

                await _products.UpdateOneAsync(filter, update);
            }
            else if (previousStatus == StockReservationStatus.Confirmed)
            {
                // Nếu đã Confirmed (ví dụ hoàn hàng RTO từ đơn vị vận chuyển 3PL về kho): Cộng lại Quantity khả dụng
                var filter = Builders<ShoeProduct>.Filter.And(
                    Builders<ShoeProduct>.Filter.Eq(p => p.Id, item.ProductId),
                    Builders<ShoeProduct>.Filter.ElemMatch(p => p.Sizes, s => s.Size == item.Size));

                var update = Builders<ShoeProduct>.Update
                    .Inc("sizes.$.quantity", item.Quantity)
                    .Set(p => p.UpdatedAt, now);

                await _products.UpdateOneAsync(filter, update);
            }
        }

        _logger.LogInformation(
            "Đã hoàn trả tồn kho ({TargetStatus}) cho OrderId={OrderId} (trạng thái trước: {PrevStatus}). Lý do: {Reason}",
            targetStatus, orderId, previousStatus, req.Reason);

        return new StockOperationResult(
            Success: true,
            Status: targetStatus,
            Message: $"Đã hoàn trả {released.TotalQuantity} đôi giày của đơn '{orderId}' về lại kho thành công.",
            IsIdempotentReplay: false,
            Reservation: released);
    }

    public async Task<StockReservation?> GetReservationByOrderIdAsync(string orderId)
    {
        return await _reservations.Find(r => r.OrderId == orderId.Trim()).FirstOrDefaultAsync();
    }

    public async Task<List<StockReservation>> GetReservationsAsync(string? status = null, int limit = 50)
    {
        var filter = string.IsNullOrWhiteSpace(status)
            ? Builders<StockReservation>.Filter.Empty
            : Builders<StockReservation>.Filter.Eq(r => r.Status, status.Trim());

        return await _reservations.Find(filter)
            .SortByDescending(r => r.CreatedAt)
            .Limit(Math.Clamp(limit, 1, 200))
            .ToListAsync();
    }

    /// <summary>
    /// Quét và tự động thu hồi tồn kho của các phiên giữ hàng đã quá hạn 15 phút (Self-Healing TTL Sweeper).
    /// </summary>
    public async Task<int> ReleaseExpiredReservationsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var expiredList = await _reservations
            .Find(r => r.Status == StockReservationStatus.Reserved && r.ExpiresAt <= now)
            .Limit(100)
            .ToListAsync(cancellationToken);

        var releasedCount = 0;
        foreach (var expired in expiredList)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var res = await ReleaseStockAsync(
                new ReleaseStockRequest(expired.OrderId, "TTL_EXPIRED_AUTO_SWEEP"),
                isExpiredByWorker: true);

            if (res.Success && !res.IsIdempotentReplay)
            {
                releasedCount++;
            }
        }

        return releasedCount;
    }

    // ===================== PRIVATE HELPERS =====================

    private async Task RollbackLockedItemsAsync(List<ReservedStockItem> lockedItems)
    {
        var now = DateTime.UtcNow;
        foreach (var locked in lockedItems)
        {
            try
            {
                var filter = Builders<ShoeProduct>.Filter.And(
                    Builders<ShoeProduct>.Filter.Eq(p => p.Id, locked.ProductId),
                    Builders<ShoeProduct>.Filter.ElemMatch(p => p.Sizes, s => s.Size == locked.Size));

                var update = Builders<ShoeProduct>.Update
                    .Inc("sizes.$.quantity", locked.Quantity)
                    .Inc("sizes.$.reservedQuantity", -locked.Quantity)
                    .Set(p => p.UpdatedAt, now);

                await _products.UpdateOneAsync(filter, update);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi rollback tồn kho cho ProductId={ProductId}, Size={Size}, Qty={Qty}",
                    locked.ProductId, locked.Size, locked.Quantity);
            }
        }
    }

    private async Task<StockFailureDetail> DiagnoseStockFailureAsync(ReserveStockItemRequest item)
    {
        var product = await _products.Find(p => p.Id == item.ProductId).FirstOrDefaultAsync();
        if (product == null)
        {
            return new StockFailureDetail(
                item.ProductId,
                null,
                item.Size,
                item.Quantity,
                0,
                "PRODUCT_NOT_FOUND",
                $"Không tìm thấy sản phẩm với ID '{item.ProductId}'.");
        }

        if (!product.IsActive)
        {
            return new StockFailureDetail(
                item.ProductId,
                product.Name,
                item.Size,
                item.Quantity,
                0,
                "PRODUCT_INACTIVE",
                $"Sản phẩm '{product.Name}' hiện đã ngừng kinh doanh.");
        }

        var sizeEntry = product.Sizes.FirstOrDefault(s => s.Size == item.Size);
        if (sizeEntry == null)
        {
            return new StockFailureDetail(
                item.ProductId,
                product.Name,
                item.Size,
                item.Quantity,
                0,
                "SIZE_NOT_AVAILABLE",
                $"Mẫu giày '{product.Name}' không có cỡ (Size) {item.Size}.");
        }

        return new StockFailureDetail(
            item.ProductId,
            product.Name,
            item.Size,
            item.Quantity,
            sizeEntry.Quantity,
            "OUT_OF_STOCK",
            $"Mẫu giày '{product.Name}' (Size {item.Size}) chỉ còn {sizeEntry.Quantity} đôi khả dụng, không đủ {item.Quantity} đôi theo yêu cầu.");
    }
}

