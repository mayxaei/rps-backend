
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using rps_backend.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace rps_backend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly PasswordHasher<Player> _passwordHasher = new();

        public AuthController(
            ApplicationDbContext context,
            IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            var nickname = request.Nickname?.Trim() ?? "";
            var password = request.Password ?? "";
            var confirmPassword = request.ConfirmPassword ?? "";

            if (nickname.Length < 3 || nickname.Length > 30)
                return BadRequest(new { message = "Nick musi mieć od 3 do 30 znaków." });

            if (!Regex.IsMatch(nickname, @"^[a-zA-Z0-9_-]+$"))
                return BadRequest(new { message = "Nick może zawierać tylko litery, cyfry, _ i -." });

            var passwordError = ValidatePassword(password);
            if (passwordError != null)
                return BadRequest(new { message = passwordError });

            if (password != confirmPassword)
                return BadRequest(new { message = "Podane hasła nie są identyczne." });

            var player = await _context.Players.FirstOrDefaultAsync(
                p => p.Nickname.ToLower() == nickname.ToLower());

            if (player != null && !string.IsNullOrEmpty(player.PasswordHash))
                return Conflict(new { message = "Ten nick jest już zajęty." });

            if (player == null)
            {
                player = new Player
                {
                    Nickname = nickname,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Players.Add(player);
            }

            player.PasswordHash = _passwordHasher.HashPassword(player, password);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Konto zostało utworzone. Możesz się zalogować."
            });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var nickname = request.Nickname?.Trim() ?? "";
            var password = request.Password ?? "";

            if (string.IsNullOrWhiteSpace(nickname) ||
                string.IsNullOrWhiteSpace(password))
            {
                return BadRequest(new { message = "Podaj nick i hasło." });
            }

            var player = await _context.Players.FirstOrDefaultAsync(
                p => p.Nickname.ToLower() == nickname.ToLower());

            if (player == null || string.IsNullOrEmpty(player.PasswordHash))
                return Unauthorized(new { message = "Nieprawidłowy nick lub hasło." });

            var verification = _passwordHasher.VerifyHashedPassword(
                player, player.PasswordHash, password);

            if (verification == PasswordVerificationResult.Failed)
                return Unauthorized(new { message = "Nieprawidłowy nick lub hasło." });

            var token = GenerateToken(player);

            return Ok(new
            {
                id = player.Id,
                nickname = player.Nickname,
                token
            });
        }

        private string GenerateToken(Player player)
        {
            var key = _configuration["Jwt:Key"]
                ?? throw new InvalidOperationException("Brak konfiguracji Jwt:Key.");

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, player.Id.ToString()),
                new Claim(ClaimTypes.Name, player.Nickname)
            };

            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(
                    _configuration.GetValue<int>("Jwt:ExpiryMinutes", 120)),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private static string? ValidatePassword(string password)
        {
            if (password.Length < 8 || password.Length > 100)
                return "Hasło musi mieć od 8 do 100 znaków.";

            if (!password.Any(char.IsUpper))
                return "Hasło musi zawierać przynajmniej jedną wielką literę.";

            if (!password.Any(char.IsLower))
                return "Hasło musi zawierać przynajmniej jedną małą literę.";

            if (!password.Any(char.IsDigit))
                return "Hasło musi zawierać przynajmniej jedną cyfrę.";

            return null;
        }
    }

    public class LoginRequest
    {
        public string Nickname { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class RegisterRequest
    {
        public string Nickname { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
