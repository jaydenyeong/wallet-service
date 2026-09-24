namespace Wallet.Api.Auth;

public sealed class JwtOptions
{
    public string Issuer {get; set;} = "";
    public string Audience {get; set;} = "";
    public string SigningKey {get; set;} = "";
    public int ExpiryMinutes {get; set;} = 60;
}