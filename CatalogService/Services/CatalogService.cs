using CatalogService.Dtos;
using CatalogService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CatalogService.Services;

public interface ICatalogService
{
    Task<PagedResult<ShoeProduct>> GetProductsAsync(ProductFilterQuery query);
    Task<ShoeProduct?> GetProductByIdAsync(string id);
    Task<ShoeProduct> CreateProductAsync(CreateProductRequest req);
    Task<ShoeProduct?> UpdateProductAsync(string id, UpdateProductRequest req);
    Task<bool> DeleteProductAsync(string id);
    Task<ShoeProduct?> AddReviewAsync(string id, string userId, string fullName, AddReviewRequest req);
    Task<CatalogFiltersResponse> GetFiltersAsync();
}

public class ProductCatalogService : ICatalogService
{
    private readonly IMongoCollection<ShoeProduct> _products;

    public ProductCatalogService(IMongoDatabase database)
    {
        _products = database.GetCollection<ShoeProduct>("Products");
    }

    public async Task<PagedResult<ShoeProduct>> GetProductsAsync(ProductFilterQuery query)
    {
        var filterBuilder = Builders<ShoeProduct>.Filter;
        var filter = filterBuilder.Eq(p => p.IsActive, true);

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

    public async Task<ShoeProduct?> GetProductByIdAsync(string id)
    {
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
            Sizes = req.Sizes?.Select(s => new SizeStock { Size = s.Size, Quantity = s.Quantity }).ToList() ?? new(),
            ThumbnailUrl = req.ThumbnailUrl.Trim(),
            ImageUrls = req.ImageUrls ?? new(),
            Tags = req.Tags ?? new(),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        await _products.InsertOneAsync(product);
        return product;
    }

    public async Task<ShoeProduct?> UpdateProductAsync(string id, UpdateProductRequest req)
    {
        var update = Builders<ShoeProduct>.Update
            .Set(p => p.Name, req.Name.Trim())
            .Set(p => p.Category, req.Category.Trim())
            .Set(p => p.Gender, string.IsNullOrWhiteSpace(req.Gender) ? "Unisex" : req.Gender.Trim())
            .Set(p => p.Description, req.Description.Trim())
            .Set(p => p.Price, req.Price)
            .Set(p => p.SalePrice, req.SalePrice)
            .Set(p => p.Color, req.Color.Trim())
            .Set(p => p.Sizes, req.Sizes?.Select(s => new SizeStock { Size = s.Size, Quantity = s.Quantity }).ToList() ?? new())
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

    public async Task<bool> DeleteProductAsync(string id)
    {
        // Xóa mềm: đánh dấu IsActive = false
        var result = await _products.UpdateOneAsync(
            p => p.Id == id,
            Builders<ShoeProduct>.Update.Set(p => p.IsActive, false));
        return result.ModifiedCount > 0;
    }

    public async Task<ShoeProduct?> AddReviewAsync(string id, string userId, string fullName, AddReviewRequest req)
    {
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
}
