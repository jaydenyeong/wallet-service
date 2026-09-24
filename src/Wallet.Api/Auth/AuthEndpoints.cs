using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wallet.Api.Data;

namespace Wallet.Api.Auth;

public sealed record RegisterRequest(string Email, string Password);
public sealed record RegisterResponse(Guid UserId, string Email);
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");
        group.MapPost("/register", Register);
        group.MapPost("/login", Login);

        app.MapGet("/api/me", Me).RequireAuthorization().WithTags("Auth");
        return app;
    }

    private static async Task<IResult> Register(
        RegisterRequest req, WalletDbContext db, IPasswordHasher<User> hasher, CancellationToken ct)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var errors = new Dictionary<string, string[]>();
        if (!MailAddress.TryCreate(email, out _)) errors["email"] = ["Invalid email."];
        if ((req.Password ?? "").Length < 8) errors["password"] = ["Password must be at least 8 characters."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Results.Problem(statusCode: 409, title: "Email already registered");

        var now = DateTimeOffset.UtcNow;
        var user = new User {Id = Guid.CreateVersion7(), Email = email, CreatedAt = now};
        user.PasswordHash = hasher.HashPassword(user, req.Password!);

        db.Users.Add(user);
        db.Accounts.Add(new Account
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            Kind = AccountKind.UserWallet,
            Currency = "MYR",
            Balance = 0,
            CreatedAt = now,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException {SqlState: PostgresErrorCodes.UniqueViolation})
        {
            return Results.Problem(statusCode: 409, title: "Email already registered");
        }
        return Results.Created("/api/me", new RegisterResponse(user.Id, user.Email));
    }

    private static async Task<IResult> Login(
        LoginRequest req, WalletDbContext db, IPasswordHasher<User> hasher, TokenService tokens, CancellationToken ct)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);

        if (user is null ||
            hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "") == PasswordVerificationResult.Failed)
        {
            return Results.Problem(statusCode: 401, title: "Invalid email or password");
        }

        var (token, expiresAt) = tokens.Create(user);
        return Results.Ok(new LoginResponse(token, expiresAt));
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, WalletDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        return Results.Ok(new RegisterResponse(user.Id, user.Email));
    }
}