using Fractals.Data.Entities;

namespace Fractals.Api.Services;

public record RegisterRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, Guid UserId, string Username, string Role);

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);
    string GenerateJwt(UserEntity user);
}

