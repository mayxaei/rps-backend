
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using rps_backend.Data;

namespace rps_backend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RankingController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public RankingController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetRanking(
            [FromQuery] string sortBy = "wins")
        {
            if (sortBy != "wins" && sortBy != "winRate")
            {
                return BadRequest(new
                {
                    message = "Nieprawidłowy sposób sortowania."
                });
            }

            var players = await _context.Players
                .AsNoTracking()
                .ToListAsync();

            var games = await _context.Games
                .AsNoTracking()
                .ToListAsync();

            var ranking = players.Select(player =>
            {
                var playerGames = games
                    .Where(game =>
                        game.PlayerAId == player.Id ||
                        game.PlayerBId == player.Id)
                    .ToList();

                var matches = playerGames.Count;

                var wins = playerGames.Count(game =>
                    game.WinnerId == player.Id);

                var losses = matches - wins;

                var winRate = matches == 0
                    ? 0
                    : Math.Round((double)wins / matches * 100, 2);

                return new
                {
                    player.Id,
                    player.Nickname,
                    Matches = matches,
                    Wins = wins,
                    Losses = losses,
                    WinRate = winRate
                };
            });

            var sortedRanking = sortBy == "winRate"
                ? ranking
                    .OrderByDescending(player => player.WinRate)
                    .ThenByDescending(player => player.Wins)
                    .ThenBy(player => player.Nickname)
                    .ToList()
                : ranking
                    .OrderByDescending(player => player.Wins)
                    .ThenByDescending(player => player.WinRate)
                    .ThenBy(player => player.Nickname)
                    .ToList();

            var result = sortedRanking.Select((player, index) => new
            {
                Place = index + 1,
                player.Id,
                player.Nickname,
                player.Matches,
                player.Wins,
                player.Losses,
                player.WinRate
            });

            return Ok(result);
        }
    }
}
