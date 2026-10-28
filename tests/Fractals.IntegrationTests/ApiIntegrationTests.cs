using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Fractals.Api.Services;
using Fractals.Core.Models;
using Xunit;

namespace Fractals.IntegrationTests;

public class ApiIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<string> RegisterAndLoginAsync(string username)
    {
        var password = "SecurePassword123!";
        var regRes = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, password));
        regRes.EnsureSuccessStatusCode();

        var loginRes = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
        loginRes.EnsureSuccessStatusCode();

        var auth = await loginRes.Content.ReadFromJsonAsync<AuthResponse>();
        return auth!.Token;
    }

    [Fact]
    public async Task ServedByHeader_IsPresentOnAllResponses()
    {
        var response = await _client.GetAsync("/healthz");
        response.EnsureSuccessStatusCode();

        response.Headers.Should().ContainKey("X-Served-By");
    }

    [Fact]
    public async Task RegisterAndLogin_ReturnsValidTokenAndUser()
    {
        string username = $"user_{Guid.NewGuid():N}";
        string token = await RegisterAndLoginAsync(username);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(username);
    }

    [Fact]
    public async Task CreateTask_ExceedingLimit_Returns422()
    {
        string token = await RegisterAndLoginAsync($"user_{Guid.NewGuid():N}");

        var heavyParams = new FractalParameters
        {
            Width = 3000,
            Height = 3000,
            MaxIterations = 5000 // 3000 * 3000 * 5000 = 45,000,000,000 > 500,000,000 limit
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(heavyParams);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("перевищує встановлений ліміт");
    }

    [Fact]
    public async Task CreateTask_ValidParameters_Returns202Accepted()
    {
        string token = await RegisterAndLoginAsync($"user_{Guid.NewGuid():N}");

        var validParams = new FractalParameters
        {
            Type = FractalType.Mandelbrot,
            Width = 400,
            Height = 300,
            MaxIterations = 100
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(validParams);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("queuePosition");
        body.Should().Contain("Queued");
    }

    [Fact]
    public async Task Idor_CannotAccessOtherUserTask_ReturnsForbidden()
    {
        string user1Token = await RegisterAndLoginAsync($"user1_{Guid.NewGuid():N}");
        string user2Token = await RegisterAndLoginAsync($"user2_{Guid.NewGuid():N}");

        // User 1 creates task
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/tasks");
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user1Token);
        createReq.Content = JsonContent.Create(new FractalParameters { Width = 200, Height = 200, MaxIterations = 50 });
        var createRes = await _client.SendAsync(createReq);
        createRes.EnsureSuccessStatusCode();

        var taskDoc = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        string taskId = taskDoc.GetProperty("id").GetString()!;

        // User 2 attempts to cancel User 1's task
        using var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"/api/tasks/{taskId}/cancel");
        cancelReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user2Token);

        var cancelRes = await _client.SendAsync(cancelReq);
        cancelRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminEndpoint_ForbiddenForRegularUser()
    {
        string regularToken = await RegisterAndLoginAsync($"regular_{Guid.NewGuid():N}");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/metrics");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", regularToken);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
