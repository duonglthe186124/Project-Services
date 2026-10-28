using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using OrderService.Data;
using OrderService.Dtos;
using OrderService.Models;

namespace OrderService.Services;

/// <summary>Truy vấn đơn hàng (chỉ đọc). Tách khỏi orchestrator để giữ orchestrator tập trung vào chuyển trạng thái.</summary>
public interface IOrderQueryService
{
    Task<PagedResult<OrderSummaryDto>> GetByUserAsync(string userId, int page, int pageSize, CancellationToken ct = default);
    Task<PagedResult<OrderSummaryDto>> GetAllAsync(string? status, int page, int pageSize, CancellationToken ct = default);
    Task<(Order? Order, OrderSagaState? Saga)> GetWithSagaAsync(string orderId, CancellationToken ct = default);
}

public class OrderQueryService : IOrderQueryService
{
    public const int MaxPageSize = 50;

    private readonly IMongoCollection<Order> _orders;
    private readonly IMongoCollection<OrderSagaState> _sagas;

    public OrderQueryService(IMongoDatabase database)
    {
        _orders = database.GetCollection<Order>(OrderDbInitializer.OrdersCollectionName);
        _sagas = database.GetCollection<OrderSagaState>(OrderDbInitializer.OrderSagaStatesCollectionName);
    }

    public static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    public Task<PagedResult<OrderSummaryDto>> GetByUserAsync(string userId, int page, int pageSize, CancellationToken ct = default) =>
        QueryAsync(Builders<Order>.Filter.Eq(o => o.UserId, userId), page, pageSize, ct);

    public Task<PagedResult<OrderSummaryDto>> GetAllAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(status)
            ? Builders<Order>.Filter.Empty
            : Builders<Order>.Filter.Eq(o => o.OrderStatus, status.Trim());
        return QueryAsync(filter, page, pageSize, ct);
    }

    public async Task<(Order? Order, OrderSagaState? Saga)> GetWithSagaAsync(string orderId, CancellationToken ct = default)
    {
        var order = await _orders.Find(o => o.Id == orderId).FirstOrDefaultAsync(ct);
        if (order is null) return (null, null);
        var saga = await _sagas.Find(s => s.OrderId == orderId).FirstOrDefaultAsync(ct);
        return (order, saga);
    }

    private async Task<PagedResult<OrderSummaryDto>> QueryAsync(FilterDefinition<Order> filter, int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = NormalizePaging(page, pageSize);
        var total = await _orders.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _orders.Find(filter)
            .SortByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return new PagedResult<OrderSummaryDto>(items.Select(OrderSummaryDto.FromDomain).ToList(), page, pageSize, total);
    }
}
