namespace CatalogService.Services;

/// <summary>
/// Background Worker chạy ngầm mỗi 30 giây để tự động thu hồi tồn kho của các phiên giữ hàng (StockReservations)
/// đã quá hạn TTL (mặc định 15 phút) nhưng chưa được thanh toán.
/// Đây là chốt chặn tự phục hồi (Self-Healing Guard) bảo vệ CatalogDb ngay cả khi OrderService bị gián đoạn.
/// </summary>
public class StockReservationExpirationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StockReservationExpirationWorker> _logger;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    public StockReservationExpirationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<StockReservationExpirationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StockReservationExpirationWorker đã khởi động (Chu kỳ quét TTL: {Seconds}s).", SweepInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                var catalogService = scope.ServiceProvider.GetRequiredService<ICatalogService>();
                var releasedCount = await catalogService.ReleaseExpiredReservationsAsync(stoppingToken);

                if (releasedCount > 0)
                {
                    _logger.LogInformation(
                        "Self-Healing TTL Sweeper: Đã tự động nhả tồn kho cho {Count} phiên giữ hàng quá hạn thanh toán.",
                        releasedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi tạm thời khi quét các phiên giữ hàng quá hạn trong StockReservationExpirationWorker.");
            }
        }
    }
}
