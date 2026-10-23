
namespace rps_backend.Data
{
    public class Game
    {
        public int Id { get; set; }
        public int PlayerAId { get; set; }
        public int PlayerBId { get; set; }
        public int? WinnerId { get; set; }
        public bool IsDraw { get; set; }
        public DateTime PlayedAt { get; set; } = DateTime.UtcNow;

        public Player PlayerA { get; set; } = null!;
        public Player PlayerB { get; set; } = null!;
        public Player? Winner { get; set; }
    }
}
