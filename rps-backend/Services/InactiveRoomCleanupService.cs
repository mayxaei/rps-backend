
using Microsoft.EntityFrameworkCore;
using rps_backend.Data;

namespace rps_backend.Services
{
    public class InactiveRoomCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<InactiveRoomCleanupService> _logger;

        private static readonly TimeSpan CheckInterval =
            TimeSpan.FromMinutes(5);

        private static readonly TimeSpan RoomRetention =
            TimeSpan.FromMinutes(30);

        public InactiveRoomCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<InactiveRoomCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Uruchomiono automatyczne czyszczenie nieaktywnych pokoi.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupInactiveRoomsAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Błąd podczas czyszczenia nieaktywnych pokoi.");
                }

                try
                {
                    await Task.Delay(CheckInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task CleanupInactiveRoomsAsync(
            CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();

            var db = scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>();

            var cutoff = DateTime.UtcNow - RoomRetention;

            var inactiveRooms = await db.GameRooms
                .Where(room =>
                    !room.IsActive &&
                    room.DeactivatedAt != null &&
                    room.DeactivatedAt < cutoff)
                .ToListAsync(cancellationToken);

            if (inactiveRooms.Count == 0)
            {
                _logger.LogDebug(
                    "Nie znaleziono nieaktywnych pokoi do usunięcia.");

                return;
            }

            db.GameRooms.RemoveRange(inactiveRooms);

            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Usunięto {Count} nieaktywnych pokoi z bazy danych.",
                inactiveRooms.Count);
        }
    }
}
