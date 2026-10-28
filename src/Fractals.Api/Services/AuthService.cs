using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fractals.Data;
using Fractals.Data.Entities;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Fractals.Api.Services;

public class AuthService : IAuthService
{
    private readonly FractalsDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        FractalsDbContext dbContext,
        IConfiguration configuration,
        ILogger<AuthService> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        string username = request.Username.Trim();
        if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
        {
            throw new ArgumentException("Ім'я користувача повинно містити щонайменше 3 символи.");
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
        {
            throw new ArgumentException("Пароль повинен містити щонайменше 6 символів.");
        }

        bool exists = await _dbContext.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"Користувач із ім'ям '{username}' вже існує.");
        }

        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = HashPassword(request.Password),
            Role = "user",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Зареєстровано нового користувача: {Username}", user.Username);

        string token = GenerateJwt(user);
        return new AuthResponse(token, user.Id, user.Username, user.Role);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        string username = request.Username.Trim();
        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);

        if (user == null || !VerifyPassword(request.Password, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Невірне ім'я користувача або пароль.");
        }

        string token = GenerateJwt(user);
        return new AuthResponse(token, user.Id, user.Username, user.Role);
    }

    public string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 2,
            MemorySize = 65536,
            Iterations = 4
        };

        byte[] hash = argon2.GetBytes(32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        try
        {
            var parts = storedHash.Split('.');
            if (parts.Length != 2) return false;

            byte[] salt = Convert.FromBase64String(parts[0]);
            byte[] expectedHash = Convert.FromBase64String(parts[1]);

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                DegreeOfParallelism = 2,
                MemorySize = 65536,
                Iterations = 4
            };

            byte[] actualHash = argon2.GetBytes(32);
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }

    public string GenerateJwt(UserEntity user)
    {
        var secret = _configuration["Jwt:Secret"] ?? "SuperSecretLongSecureKeyForJwtTokens1234567890!@#$";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"] ?? "Fractals.Api",
            audience: _configuration["Jwt:Audience"] ?? "Fractals.Client",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

