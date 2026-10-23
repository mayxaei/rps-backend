
namespace rps_backend.Data
{
    public class GameRoom
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public int HostPlayerId { get; set; }
        public int? GuestPlayerId { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? DeactivatedAt { get; set; }

        public Player HostPlayer { get; set; } = null!;
        public Player? GuestPlayer { get; set; }
    }
}