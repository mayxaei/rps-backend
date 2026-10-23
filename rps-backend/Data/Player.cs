
namespace rps_backend.Data
{
    public class Player
    {
        public int Id { get; set; }
        public string Nickname { get; set; } = string.Empty;
        public string? PasswordHash { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
