using System.Security.Claims;
using HeatLab.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HeatLab.Gateway;

public record Credentials(string Username, string Password);

[ApiController]
[Route("api/auth")]
public class AuthController(AppDb db) : ControllerBase
{
    private static readonly PasswordHasher<User> Hasher = new();

    [HttpPost("register")]
    public async Task<IActionResult> Register(Credentials c)
    {
        string name = (c.Username ?? "").Trim();
        if (name.Length < 3) return BadRequest(new { message = "Ім'я має містити щонайменше 3 символи." });
        if (string.IsNullOrEmpty(c.Password)) return BadRequest(new { message = "Введіть пароль." });
        if (await db.Users.AnyAsync(u => u.Username == name))
            return Conflict(new { message = "Таке ім'я вже зайняте." });

        var user = new User { Username = name };
        user.PasswordHash = Hasher.HashPassword(user, c.Password!);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await SignInAsync(user);
        return Ok(new { user.Username, user.Role });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(Credentials c)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == (c.Username ?? "").Trim());
        if (user == null || Hasher.VerifyHashedPassword(user, user.PasswordHash, c.Password ?? "") == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Невірне ім'я або пароль." });

        await SignInAsync(user);
        return Ok(new { user.Username, user.Role });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return Ok();
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        username = User.Identity!.Name,
        role = User.FindFirstValue(ClaimTypes.Role)
    });

    private Task SignInAsync(User user)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return HttpContext.SignInAsync(new ClaimsPrincipal(identity));
    }
}
