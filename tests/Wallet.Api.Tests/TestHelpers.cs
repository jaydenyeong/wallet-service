using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Wallet.Api.Tests;

public static class TestHelpers
{
    private sealed record LoginResponse(string AccessToken);
    private sealed record BalanceResponse(long Balance);

    public static async Task<(HttpClient Client, string Email)> CreateUserAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@test.local";
        var body = new { email, password = "Passw0rd!" };

        (await client.PostAsJsonAsync("/api/auth/register", body)).EnsureSuccessStatusCode();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", body))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<LoginResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, email);
    }

    public static Task<HttpResponseMessage> TopupAsync(HttpClient c, long amount, string? key = null) =>
        SendAsync(c, "/api/wallet/topups", new { amount }, key);

    public static Task<HttpResponseMessage> TransferAsync(HttpClient c, string toEmail, long amount, string? key = null) =>
        SendAsync(c, "/api/wallet/transfers", new { toEmail, amount }, key);

    public static async Task<long> GetBalanceAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<BalanceResponse>("/api/wallet"))!.Balance;

    private static Task<HttpResponseMessage> SendAsync(HttpClient c, string url, object body, string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return c.SendAsync(req);
    }

}