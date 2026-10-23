
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using rps_backend.Data;
using System.Security.Claims;
using System.Security.Cryptography;

namespace rps_backend.Controllers
{
    [ApiController]
    [Route("api/rooms")]
    [Authorize]
    public class RoomsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public RoomsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateRoom()
        {
            var player = await GetCurrentPlayer();

            if (player == null)
                return Unauthorized(new { message = "Nie znaleziono zalogowanego gracza." });

            string code;

            do
            {
                code = RandomNumberGenerator.GetString(
                    "ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6);
            }
            while (await _context.GameRooms.AnyAsync(r => r.Code == code && r.IsActive));

            var room = new GameRoom
            {
                Code = code,
                HostPlayerId = player.Id,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.GameRooms.Add(room);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                roomCode = room.Code,
                role = "A",
                message = "Pokój został utworzony."
            });
        }

        [HttpPost("join")]
        public async Task<IActionResult> JoinRoom([FromBody] JoinRoomRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Code))
                return BadRequest(new { message = "Wpisz kod pokoju." });

            var code = request.Code.Trim().ToUpperInvariant();
            var player = await GetCurrentPlayer();

            if (player == null)
                return Unauthorized(new { message = "Nie znaleziono zalogowanego gracza." });

            var room = await _context.GameRooms
                .FirstOrDefaultAsync(r => r.Code == code && r.IsActive);

            if (room == null)
                return NotFound(new { message = "Pokój o podanym kodzie nie istnieje." });

            if (room.HostPlayerId == player.Id)
            {
                if (room.GuestPlayerId.HasValue)
                    return BadRequest(new { message = "Jesteś hostem tego pokoju, a drugie miejsce jest zajęte." });

                return Ok(new
                {
                    roomCode = room.Code,
                    role = "A",
                    message = "Ponownie dołączono do własnego pokoju."
                });
            }

            if (room.GuestPlayerId.HasValue)
                return Conflict(new { message = "Ten pokój jest już pełny." });

            room.GuestPlayerId = player.Id;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                roomCode = room.Code,
                role = "B",
                message = "Dołączono do pokoju."
            });
        }

        [HttpGet("{code}")]
        public async Task<IActionResult> GetRoom(string code)
        {
            var player = await GetCurrentPlayer();

            if (player == null)
                return Unauthorized();

            var room = await _context.GameRooms
                .AsNoTracking()
                .Include(r => r.HostPlayer)
                .Include(r => r.GuestPlayer)
                .FirstOrDefaultAsync(r =>
                    r.Code == code.Trim().ToUpperInvariant() && r.IsActive);

            if (room == null)
                return NotFound(new { message = "Pokój nie istnieje lub został zamknięty." });

            if (room.HostPlayerId != player.Id && room.GuestPlayerId != player.Id)
                return Forbid();

            return Ok(new
            {
                roomCode = room.Code,
                hostNickname = room.HostPlayer.Nickname,
                guestNickname = room.GuestPlayer?.Nickname,
                isFull = room.GuestPlayerId.HasValue
            });
        }

        private async Task<Player?> GetCurrentPlayer()
        {
            var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!int.TryParse(idValue, out var playerId))
                return null;

            return await _context.Players
                .FirstOrDefaultAsync(p => p.Id == playerId);
        }
    }

    public class JoinRoomRequest
    {
        public string Code { get; set; } = string.Empty;
    }
}
