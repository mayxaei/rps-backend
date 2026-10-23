using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using rps_backend.Data;
using System.Security.Claims;

namespace rps_backend
{
    [Authorize]
    public class CounterHub : Hub
    {
        private const int WinningScore = 3;
        private static readonly object LockObject = new();
        private static readonly Dictionary<string, RoomState> Rooms = new();
        private static readonly Dictionary<string, string> ConnectionRooms = new();
        private static readonly Dictionary<string, string> ConnectionRoles = new();

        private readonly ApplicationDbContext _context;

        public CounterHub(ApplicationDbContext context)
        {
            _context = context;
        }

        private sealed class RoomState
        {
            public string Code { get; set; } = "";
            public int PlayerAId { get; set; }
            public int? PlayerBId { get; set; }
            public string PlayerAName { get; set; } = "Gracz A";
            public string PlayerBName { get; set; } = "Oczekiwanie na gracza";
            public string? ConnectionA { get; set; }
            public string? ConnectionB { get; set; }
            public int ScoreA { get; set; }
            public int ScoreB { get; set; }
            public int Round { get; set; } = 1;
            public string Result { get; set; } = "";
            public bool GameFinished { get; set; }
            public bool GameSaved { get; set; }
            public Dictionary<string, string> Moves { get; } = new();
        }

        public override async Task OnConnectedAsync()
        {
            var http = Context.GetHttpContext();
            var code = http?.Request.Query["roomCode"]
                .ToString().Trim().ToUpperInvariant();
            var requestedRole = http?.Request.Query["role"]
                .ToString().ToUpperInvariant();
            var idValue = Context.User?
                .FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(code) ||
                (requestedRole != "A" && requestedRole != "B") ||
                !int.TryParse(idValue, out var playerId))
            {
                Context.Abort();
                return;
            }

            var dbRoom = await _context.GameRooms
                .AsNoTracking()
                .Include(r => r.HostPlayer)
                .Include(r => r.GuestPlayer)
                .FirstOrDefaultAsync(r => r.Code == code && r.IsActive);

            if (dbRoom == null)
            {
                Context.Abort();
                return;
            }

            string actualRole;

            if (dbRoom.HostPlayerId == playerId)
                actualRole = "A";
            else if (dbRoom.GuestPlayerId == playerId)
                actualRole = "B";
            else
            {
                Context.Abort();
                return;
            }

            lock (LockObject)
            {
                if (!Rooms.TryGetValue(code, out var state))
                {
                    state = new RoomState
                    {
                        Code = code,
                        PlayerAId = dbRoom.HostPlayerId,
                        PlayerBId = dbRoom.GuestPlayerId,
                        PlayerAName = dbRoom.HostPlayer.Nickname,
                        PlayerBName = dbRoom.GuestPlayer?.Nickname
                            ?? "Oczekiwanie na gracza"
                    };

                    Rooms[code] = state;
                }
                else
                {
                    if (state.PlayerAId != dbRoom.HostPlayerId)
                    {
                        Context.Abort();
                        return;
                    }

                    if (state.PlayerBId == null &&
                        dbRoom.GuestPlayerId.HasValue)
                    {
                        state.PlayerBId = dbRoom.GuestPlayerId;
                        state.PlayerBName =
                            dbRoom.GuestPlayer?.Nickname ?? "Gracz B";
                    }

                    if (state.PlayerBId != dbRoom.GuestPlayerId)
                    {
                        Context.Abort();
                        return;
                    }
                }

                if (actualRole == "A")
                {
                    if (state.ConnectionA != null &&
                        state.ConnectionA != Context.ConnectionId)
                    {
                        ConnectionRooms.Remove(state.ConnectionA);
                        ConnectionRoles.Remove(state.ConnectionA);
                    }

                    state.ConnectionA = Context.ConnectionId;
                    state.PlayerAName = dbRoom.HostPlayer.Nickname;
                }
                else
                {
                    if (state.ConnectionB != null &&
                        state.ConnectionB != Context.ConnectionId)
                    {
                        ConnectionRooms.Remove(state.ConnectionB);
                        ConnectionRoles.Remove(state.ConnectionB);
                    }

                    state.ConnectionB = Context.ConnectionId;
                    state.PlayerBName =
                        dbRoom.GuestPlayer?.Nickname ?? "Gracz B";
                }

                ConnectionRooms[Context.ConnectionId] = code;
                ConnectionRoles[Context.ConnectionId] = actualRole;
            }

            await base.OnConnectedAsync();
            await BroadcastGameState(code);
        }

        public async Task LeaveRoom()
        {
            await LeaveConnection(Context.ConnectionId);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await LeaveConnection(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        private async Task LeaveConnection(string connectionId)
        {
            string? code;
            string? role;
            int playerId;

            lock (LockObject)
            {
                if (!ConnectionRooms.TryGetValue(connectionId, out code) ||
                    !ConnectionRoles.TryGetValue(connectionId, out role) ||
                    !Rooms.TryGetValue(code, out var state))
                    return;

                var isActiveConnection = role == "A"
                    ? state.ConnectionA == connectionId
                    : state.ConnectionB == connectionId;

                playerId = role == "A"
                    ? state.PlayerAId
                    : state.PlayerBId ?? 0;

                ConnectionRooms.Remove(connectionId);
                ConnectionRoles.Remove(connectionId);

                if (!isActiveConnection)
                    return;
            }

            var dbRoom = await _context.GameRooms
                .FirstOrDefaultAsync(r => r.Code == code && r.IsActive);

            if (dbRoom != null)
            {
                if (role == "A" && dbRoom.HostPlayerId == playerId)
                {
                    if (dbRoom.GuestPlayerId.HasValue)
                    {
                        // Gracz B przejmuje rolę hosta.
                        dbRoom.HostPlayerId = dbRoom.GuestPlayerId.Value;
                        dbRoom.GuestPlayerId = null;
                    }
                    else
                    {
                        // Ostatni gracz opuszcza pusty pokój.
                        dbRoom.IsActive = false;
                        dbRoom.DeactivatedAt = DateTime.UtcNow;
                    }
                }
                else if (role == "B" && dbRoom.GuestPlayerId == playerId)
                {
                    dbRoom.GuestPlayerId = null;

                    bool hostConnected;

                    lock (LockObject)
                    {
                        hostConnected =
                            Rooms.TryGetValue(code!, out var state) &&
                            state.ConnectionA != null &&
                            state.ConnectionA != connectionId;
                    }

                    if (!hostConnected)
                    {
                        dbRoom.IsActive = false;
                        dbRoom.DeactivatedAt = DateTime.UtcNow;
                    }
                }

                // WAŻNE:
                // Zapisujemy również zmianę hosta po wyjściu gracza A.
                // Wcześniej SaveChangesAsync() było tylko w gałęzi gracza B,
                // więc baza nadal wskazywała starego hosta i zajęte miejsce B.
                await _context.SaveChangesAsync();

                bool roomEmpty;

                lock (LockObject)
                {
                    if (!Rooms.TryGetValue(code!, out var state))
                        return;

                    if (role == "A" &&
                        state.ConnectionA == connectionId)
                    {
                        if (state.PlayerBId.HasValue)
                        {
                            state.PlayerAId = state.PlayerBId.Value;
                            state.PlayerAName = state.PlayerBName;
                            state.ConnectionA = state.ConnectionB;

                            if (state.ConnectionA != null)
                            {
                                ConnectionRooms[state.ConnectionA] = code!;
                                ConnectionRoles[state.ConnectionA] = "A";
                            }

                            state.PlayerBId = null;
                            state.PlayerBName = "Oczekiwanie na gracza";
                            state.ConnectionB = null;
                        }
                        else
                        {
                            state.ConnectionA = null;
                        }

                        ResetRoomGame(state);
                    }
                    else if (role == "B" &&
                             state.ConnectionB == connectionId)
                    {
                        state.PlayerBId = null;
                        state.PlayerBName = "Oczekiwanie na gracza";
                        state.ConnectionB = null;

                        ResetRoomGame(state);
                    }

                    roomEmpty = state.ConnectionA == null &&
                                state.ConnectionB == null;

                    if (roomEmpty)
                        Rooms.Remove(code!);
                }

                if (roomEmpty)
                {
                    var roomToClose = await _context.GameRooms
                        .FirstOrDefaultAsync(r => r.Code == code);

                    if (roomToClose != null && roomToClose.IsActive)
                    {
                        roomToClose.IsActive = false;
                        roomToClose.DeactivatedAt = DateTime.UtcNow;

                        await _context.SaveChangesAsync();
                    }

                    return;
                }

                await BroadcastGameState(code!);
            }
        }

        private static void ResetRoomGame(RoomState state)
        {
            state.ScoreA = 0;
            state.ScoreB = 0;
            state.Round = 1;
            state.Result = "";
            state.GameFinished = false;
            state.GameSaved = false;
            state.Moves.Clear();
        }

        public async Task ChooseMove(string move)
        {
            if (move != "Rock" && move != "Paper" && move != "Scissors")
                throw new HubException("Nieprawidłowy ruch.");

            string code;
            Game? completedGame = null;

            lock (LockObject)
            {
                var state = GetCurrentRoom(out var role);
                code = state.Code;

                if (state.ConnectionA == null ||
                    state.ConnectionB == null ||
                    state.PlayerBId == null)
                    throw new HubException(
                        "Poczekaj, aż drugi gracz dołączy do pokoju.");

                if (state.GameFinished)
                    throw new HubException("Gra została już zakończona.");

                if (state.Result != "")
                    throw new HubException(
                        "Ta runda jest już zakończona.");

                if (state.Moves.ContainsKey(role))
                    throw new HubException(
                        "Wybrałeś już ruch w tej rundzie.");

                state.Moves[role] = move;

                if (state.Moves.ContainsKey("A") &&
                    state.Moves.ContainsKey("B"))
                {
                    var moveA = state.Moves["A"];
                    var moveB = state.Moves["B"];

                    if (moveA == moveB)
                    {
                        state.Result = "Remis!";
                    }
                    else if (
                        (moveA == "Rock" && moveB == "Scissors") ||
                        (moveA == "Paper" && moveB == "Rock") ||
                        (moveA == "Scissors" && moveB == "Paper"))
                    {
                        state.ScoreA++;
                        state.Result =
                            $"Rundę wygrywa {state.PlayerAName}!";
                    }
                    else
                    {
                        state.ScoreB++;
                        state.Result =
                            $"Rundę wygrywa {state.PlayerBName}!";
                    }

                    if (state.ScoreA >= WinningScore ||
                        state.ScoreB >= WinningScore)
                    {
                        state.GameFinished = true;
                        var winnerIsA = state.ScoreA >= WinningScore;

                        state.Result = winnerIsA
                            ? $"{state.PlayerAName} wygrywa całą grę!"
                            : $"{state.PlayerBName} wygrywa całą grę!";

                        if (!state.GameSaved)
                        {
                            completedGame = new Game
                            {
                                PlayerAId = state.PlayerAId,
                                PlayerBId = state.PlayerBId.Value,
                                WinnerId = winnerIsA
                                    ? state.PlayerAId
                                    : state.PlayerBId.Value,
                                IsDraw = false,
                                PlayedAt = DateTime.UtcNow
                            };

                            state.GameSaved = true;
                        }
                    }
                }
            }

            if (completedGame != null)
            {
                try
                {
                    _context.Games.Add(completedGame);
                    await _context.SaveChangesAsync();
                }
                catch
                {
                    throw new HubException(
                        "Gra została zakończona, ale nie udało się zapisać wyniku w bazie.");
                }
            }

            await BroadcastGameState(code);
        }

        public async Task NextRound()
        {
            string code;

            lock (LockObject)
            {
                var state = GetCurrentRoom(out _);
                code = state.Code;

                if (state.GameFinished)
                    throw new HubException("Gra została już zakończona.");

                if (state.Result == "")
                    throw new HubException(
                        "Poczekaj na zakończenie rundy.");

                state.Moves.Clear();
                state.Result = "";
                state.Round++;
            }

            await BroadcastGameState(code);
        }

        public async Task ResetGame()
        {
            string code;

            lock (LockObject)
            {
                var state = GetCurrentRoom(out _);
                code = state.Code;
                ResetRoomGame(state);
            }

            await BroadcastGameState(code);
        }

        private RoomState GetCurrentRoom(out string role)
        {
            if (!ConnectionRooms.TryGetValue(
                    Context.ConnectionId, out var code) ||
                !ConnectionRoles.TryGetValue(
                    Context.ConnectionId, out role!) ||
                !Rooms.TryGetValue(code, out var state))
                throw new HubException(
                    "Nie jesteś połączony z pokojem.");

            var active = role == "A"
                ? state.ConnectionA == Context.ConnectionId
                : state.ConnectionB == Context.ConnectionId;

            if (!active)
                throw new HubException(
                    "To połączenie nie jest aktywne.");

            return state;
        }

        private async Task BroadcastGameState(string code)
        {
            List<(string ConnectionId, object State)> updates = new();

            lock (LockObject)
            {
                if (!Rooms.TryGetValue(code, out var room))
                    return;

                foreach (var entry in new[]
                {
                    (Connection: room.ConnectionA, Role: "A"),
                    (Connection: room.ConnectionB, Role: "B")
                })
                {
                    if (entry.Connection == null)
                        continue;

                    var hasChosen = room.Moves.ContainsKey(entry.Role);
                    var waiting = room.PlayerBId == null ||
                                  room.ConnectionA == null ||
                                  room.ConnectionB == null;

                    var status = waiting
                        ? "Oczekiwanie na drugiego gracza."
                        : room.GameFinished
                            ? "Gra zakończona."
                            : room.Result != ""
                                ? "Runda zakończona."
                                : hasChosen
                                    ? "Twój ruch został zapisany. Czekaj na drugiego gracza."
                                    : "Wybierz swój ruch.";

                    object gameState = new
                    {
                        roomCode = room.Code,
                        role = entry.Role,
                        waitingForOpponent = waiting,
                        round = room.Round,
                        scoreA = room.ScoreA,
                        scoreB = room.ScoreB,
                        playerAName = room.PlayerAName,
                        playerBName = room.PlayerBName,
                        ownMove = room.Moves.GetValueOrDefault(entry.Role),
                        moveA = room.Result != ""
                            ? room.Moves.GetValueOrDefault("A")
                            : null,
                        moveB = room.Result != ""
                            ? room.Moves.GetValueOrDefault("B")
                            : null,
                        result = room.Result,
                        gameFinished = room.GameFinished,
                        status
                    };

                    updates.Add((entry.Connection, gameState));
                }
            }

            foreach (var update in updates)
            {
                await Clients.Client(update.ConnectionId)
                    .SendAsync("GameUpdated", update.State);
            }
        }
    }
}