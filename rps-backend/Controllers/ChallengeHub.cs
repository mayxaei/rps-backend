using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using rps_backend.Data;
using System.Security.Claims;
using System.Security.Cryptography;

namespace rps_backend
{
    [Authorize]
    public class ChallengeHub : Hub
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<int, HashSet<string>> Connections = new();
        private static readonly Dictionary<string, ChallengeInvite> PendingChallenges = new();
        private static readonly Dictionary<(int FromId, int ToId), DateTime> MutedInvites = new();
        private static readonly TimeSpan InviteMuteDuration = TimeSpan.FromMinutes(5);

        private readonly ApplicationDbContext _context;

        public ChallengeHub(ApplicationDbContext context)
        {
            _context = context;
        }

        private sealed class ChallengeInvite
        {
            public string Id { get; set; } = "";
            public int ChallengerId { get; set; }
            public string ChallengerNickname { get; set; } = "";
            public int TargetId { get; set; }
            public string TargetNickname { get; set; } = "";
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        public override async Task OnConnectedAsync()
        {
            var idValue = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!int.TryParse(idValue, out var playerId))
            {
                Console.WriteLine("[CHALLENGE] Nie udało się odczytać ID gracza z tokenu.");
                Context.Abort();
                return;
            }

            ChallengeInvite[] pending;

            lock (Sync)
            {
                if (!Connections.TryGetValue(playerId, out var connections))
                {
                    connections = new HashSet<string>();
                    Connections[playerId] = connections;
                }

                connections.Add(Context.ConnectionId);

                pending = PendingChallenges.Values
                    .Where(c => c.TargetId == playerId)
                    .ToArray();
            }

            Console.WriteLine(
                $"[CHALLENGE] Połączono gracza {playerId}, connectionId={Context.ConnectionId}");

            await base.OnConnectedAsync();

            foreach (var challenge in pending)
            {
                await Clients.Client(Context.ConnectionId).SendAsync(
                    "ChallengeReceived",
                    new
                    {
                        inviteId = challenge.Id,
                        challengerId = challenge.ChallengerId,
                        challengerNickname = challenge.ChallengerNickname
                    });
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var idValue = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (int.TryParse(idValue, out var playerId))
            {
                lock (Sync)
                {
                    if (Connections.TryGetValue(playerId, out var connections))
                    {
                        connections.Remove(Context.ConnectionId);

                        if (connections.Count == 0)
                            Connections.Remove(playerId);
                    }
                }

                Console.WriteLine(
                    $"[CHALLENGE] Rozłączono gracza {playerId}, connectionId={Context.ConnectionId}");
            }

            await base.OnDisconnectedAsync(exception);
        }

        private Task<bool> IsPlayerInActiveRoomAsync(int playerId)
        {
            return _context.GameRooms
                .AsNoTracking()
                .AnyAsync(r =>
                    r.IsActive &&
                    (r.HostPlayerId == playerId ||
                     r.GuestPlayerId == playerId));
        }

        private void RemovePendingChallenge(string inviteId)
        {
            lock (Sync)
            {
                PendingChallenges.Remove(inviteId);
            }
        }
        private bool IsInviteMuted(int fromId, int toId, out TimeSpan remaining)
        {
            lock (Sync)
            {
                var key = (fromId, toId);

                if (MutedInvites.TryGetValue(key, out var expiresAt))
                {
                    if (expiresAt > DateTime.UtcNow)
                    {
                        remaining = expiresAt - DateTime.UtcNow;
                        return true;
                    }

                    MutedInvites.Remove(key);
                }

                remaining = TimeSpan.Zero;
                return false;
            }
        }

        public async Task SendChallenge(int targetPlayerId)
        {
            var idValue = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!int.TryParse(idValue, out var challengerId))
            {
                await SendError("Nie udało się ustalić Twojego konta.");
                return;
            }
            if (IsInviteMuted(challengerId, targetPlayerId, out var remaining))
            {
                var minutes = (int)Math.Ceiling(remaining.TotalMinutes);

                await SendError(
                    $"Ten gracz odrzucił Twoje zaproszenie. Możesz zaprosić go ponownie za około {minutes} min.");
                return;
            }
            if (challengerId == targetPlayerId)
            {
                await SendError("Nie możesz wyzwać samego siebie.");
                return;
            }

            var players = await _context.Players
                .AsNoTracking()
                .Where(p => p.Id == challengerId || p.Id == targetPlayerId)
                .ToListAsync();

            var challenger = players.FirstOrDefault(p => p.Id == challengerId);
            var target = players.FirstOrDefault(p => p.Id == targetPlayerId);

            if (challenger == null || target == null)
            {
                await SendError("Nie znaleziono jednego z graczy.");
                return;
            }

            if (await IsPlayerInActiveRoomAsync(challengerId))
            {
                await SendError(
                    "Nie możesz wysłać wyzwania, ponieważ już jesteś w pokoju gry.");
                return;
            }

            if (await IsPlayerInActiveRoomAsync(targetPlayerId))
            {
                await SendError(
                    $"Gracz {target.Nickname} już uczestniczy w grze i nie może teraz przyjąć wyzwania.");
                return;
            }

            string inviteId;
            string[] targetConnections;
            string? pendingError = null;

            lock (Sync)
            {
                var expirationTime = DateTime.UtcNow.AddMinutes(-2);

                var expiredInvites = PendingChallenges
                    .Where(pair => pair.Value.CreatedAt < expirationTime)
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (var expiredInvite in expiredInvites)
                {
                    PendingChallenges.Remove(expiredInvite);
                }

                // Blokada obejmuje tylko graczy, którzy biorą udział
                // w istniejącym oczekującym zaproszeniu.
                var existingInvite = PendingChallenges.Values.FirstOrDefault(c =>
                    c.ChallengerId == challengerId ||
                    c.TargetId == challengerId ||
                    c.ChallengerId == targetPlayerId ||
                    c.TargetId == targetPlayerId);

                if (existingInvite != null)
                {
                    inviteId = "";
                    targetConnections = Array.Empty<string>();
                    pendingError = "Jeden z graczy ma już oczekujące zaproszenie.";
                }
                else
                {
                    inviteId = Guid.NewGuid().ToString("N");

                    PendingChallenges[inviteId] = new ChallengeInvite
                    {
                        Id = inviteId,
                        ChallengerId = challengerId,
                        ChallengerNickname = challenger.Nickname,
                        TargetId = targetPlayerId,
                        TargetNickname = target.Nickname
                    };

                    targetConnections = Connections.TryGetValue(
                        targetPlayerId, out var connections)
                            ? connections.ToArray()
                            : Array.Empty<string>();
                }
            }

            if (pendingError != null)
            {
                Console.WriteLine(
                    $"[CHALLENGE] Zablokowano zaproszenie: nadawca={challengerId}, " +
                    $"odbiorca={targetPlayerId}. Powód: {pendingError}");

                await SendError(pendingError);
                return;
            }

            Console.WriteLine(
                $"[CHALLENGE] Zaproszenie: nadawca={challengerId}, " +
                $"odbiorca={targetPlayerId}, połączenia odbiorcy={targetConnections.Length}, " +
                $"inviteId={inviteId}");

            if (targetConnections.Length > 0)
            {
                await Clients.Clients(targetConnections).SendAsync(
                    "ChallengeReceived",
                    new
                    {
                        inviteId,
                        challengerId,
                        challengerNickname = challenger.Nickname
                    });

                await SendMessage($"Wysłano wyzwanie do gracza {target.Nickname}.");
            }
            else
            {
                await SendMessage(
                    $"Gracz {target.Nickname} jest offline. Wyzwanie będzie oczekiwać na jego połączenie.");
            }
        }

        public async Task<bool> RespondToChallenge(
            string inviteId,
            bool accepted,
            bool muteSender = false)
        {
            var idValue = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!int.TryParse(idValue, out var playerId))
            {
                await SendError("Nie udało się ustalić Twojego konta.");
                return false;
            }

            ChallengeInvite? invite;
            string[] challengerConnections;
            string[] guestConnections;

            lock (Sync)
            {
                if (!PendingChallenges.TryGetValue(inviteId, out invite) ||
                    invite.TargetId != playerId)
                {
                    invite = null;
                }

                challengerConnections = invite != null &&
                    Connections.TryGetValue(invite.ChallengerId, out var challengerConns)
                        ? challengerConns.ToArray()
                        : Array.Empty<string>();

                guestConnections = invite != null &&
                    Connections.TryGetValue(invite.TargetId, out var guestConns)
                        ? guestConns.ToArray()
                        : Array.Empty<string>();
            }

            if (invite == null)
            {
                await SendError(
                    "To zaproszenie już nie istnieje lub nie jest przeznaczone dla Ciebie.");
                return false;
            }

            if (accepted)
            {
                var challengerBusy = await IsPlayerInActiveRoomAsync(invite.ChallengerId);
                var targetBusy = await IsPlayerInActiveRoomAsync(invite.TargetId);

                if (challengerBusy || targetBusy)
                {
                    RemovePendingChallenge(inviteId);

                    var message = challengerBusy && targetBusy
                        ? "Obaj gracze uczestniczą już w grze. Wyzwanie zostało anulowane."
                        : challengerBusy
                            ? "Gracz, który wysłał wyzwanie, uczestniczy już w innej grze."
                            : "Nie możesz zaakceptować wyzwania, ponieważ już uczestniczysz w grze.";

                    await SendError(message);

                    if (challengerConnections.Length > 0)
                    {
                        await Clients.Clients(challengerConnections).SendAsync(
                            "ChallengeError",
                            new
                            {
                                message = "Nie można rozpocząć pojedynku. Jeden z graczy uczestniczy już w innej grze."
                            });
                    }

                    return false;
                }

                if (challengerConnections.Length == 0 || guestConnections.Length == 0)
                {
                    RemovePendingChallenge(inviteId);

                    await SendError(
                        "Jeden z graczy jest offline. Wyzwanie zostało anulowane.");

                    if (challengerConnections.Length > 0)
                    {
                        await Clients.Clients(challengerConnections).SendAsync(
                            "ChallengeError",
                            new
                            {
                                message = "Wyzwanie zostało anulowane, ponieważ drugi gracz jest offline."
                            });
                    }

                    return false;
                }
            }

            lock (Sync)
            {
                if (!PendingChallenges.TryGetValue(inviteId, out var current) ||
                    current.TargetId != playerId)
                {
                    invite = null;
                }
                else
                {
                    PendingChallenges.Remove(inviteId);
                    invite = current;
                }
            }

            if (invite == null)
            {
                await SendError("Zaproszenie zostało już obsłużone.");
                return false;
            }

            if (!accepted)
            {
                if (muteSender)
                {
                    lock (Sync)
                    {
                        MutedInvites[(invite.ChallengerId, invite.TargetId)] =
                            DateTime.UtcNow.Add(InviteMuteDuration);
                    }
                }

                if (challengerConnections.Length > 0)
                {
                    await Clients.Clients(challengerConnections).SendAsync(
                        "ChallengeDeclined",
                        new { targetNickname = invite.TargetNickname });
                }

                await SendMessage(
                    muteSender
                        ? "Odrzucono wyzwanie i wyciszono gracza na 5 minut."
                        : "Odrzucono wyzwanie.");

                return true;
            }

            // Ponowna kontrola przed utworzeniem pokoju.
            if (await IsPlayerInActiveRoomAsync(invite.ChallengerId) ||
                await IsPlayerInActiveRoomAsync(invite.TargetId))
            {
                const string message =
                    "Nie można rozpocząć pojedynku, ponieważ jeden z graczy uczestniczy już w grze.";

                await SendError(message);

                if (challengerConnections.Length > 0)
                {
                    await Clients.Clients(challengerConnections).SendAsync(
                        "ChallengeError",
                        new { message });
                }

                return false;
            }

            string code;

            do
            {
                code = RandomNumberGenerator.GetString(
                    "ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6);
            }
            while (await _context.GameRooms.AnyAsync(
                r => r.Code == code && r.IsActive));

            var room = new GameRoom
            {
                Code = code,
                HostPlayerId = invite.ChallengerId,
                GuestPlayerId = invite.TargetId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.GameRooms.Add(room);
            await _context.SaveChangesAsync();

            if (challengerConnections.Length > 0)
            {
                await Clients.Clients(challengerConnections).SendAsync(
                    "ChallengeAccepted",
                    new
                    {
                        roomCode = code,
                        role = "A",
                        opponentNickname = invite.TargetNickname
                    });
            }

            if (guestConnections.Length > 0)
            {
                await Clients.Clients(guestConnections).SendAsync(
                    "ChallengeAccepted",
                    new
                    {
                        roomCode = code,
                        role = "B",
                        opponentNickname = invite.ChallengerNickname
                    });
            }

            return true;
        }

        private Task SendMessage(string message)
        {
            return Clients.Caller.SendAsync("ChallengeSent", new { message });
        }

        private Task SendError(string message)
        {
            return Clients.Caller.SendAsync("ChallengeError", new { message });
        }
    }
}