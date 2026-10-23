using Microsoft.EntityFrameworkCore;

namespace rps_backend.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Player> Players => Set<Player>();
        public DbSet<Game> Games => Set<Game>();
        public DbSet<GameRoom> GameRooms => Set<GameRoom>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Player>()
                .HasIndex(p => p.Nickname)
                .IsUnique();

            modelBuilder.Entity<Game>()
                .HasOne(g => g.PlayerA)
                .WithMany()
                .HasForeignKey(g => g.PlayerAId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Game>()
                .HasOne(g => g.PlayerB)
                .WithMany()
                .HasForeignKey(g => g.PlayerBId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Game>()
                .HasOne(g => g.Winner)
                .WithMany()
                .HasForeignKey(g => g.WinnerId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<GameRoom>()
                .HasIndex(r => r.Code)
                .IsUnique();

            modelBuilder.Entity<GameRoom>()
                .HasOne(r => r.HostPlayer)
                .WithMany()
                .HasForeignKey(r => r.HostPlayerId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<GameRoom>()
                .HasOne(r => r.GuestPlayer)
                .WithMany()
                .HasForeignKey(r => r.GuestPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
