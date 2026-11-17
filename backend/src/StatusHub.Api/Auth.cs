using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using StatusHub.Application;
using StatusHub.Domain;

namespace StatusHub.Api;

public sealed record CurrentUser(string UserId, string Email, string Slug);

public static class AuthSetup
{
    public const string DevIssuer = "statushub-dev";
    public const string DevAudience = "statushub";

    /// <summary>
    /// Production: Cognito (Auth:Authority = https://cognito-idp.eu-north-1.amazonaws.com/&lt;pool&gt;, Auth:Audience = app client id; use the ID token).
    /// Local: symmetric key (Auth:DevSigningKey) used by POST /dev/token. Never enabled without the explicit setting.
    /// </summary>
    public static void AddStatusHubAuth(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var devKey = cfg["Auth:DevSigningKey"];
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            if (!string.IsNullOrEmpty(devKey))
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = DevIssuer,
                    ValidAudience = DevAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(devKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            }
            else
            {
                o.Authority = cfg["Auth:Authority"];
                o.Audience = cfg["Auth:Audience"];
                o.TokenValidationParameters.ValidateAudience = true;
            }
        });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AccountProvisioner>();
    }

    public static string CreateDevToken(string signingKey, string email)
    {
        var sub = "dev-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToLowerInvariant())))[..16];
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(DevIssuer, DevAudience,
            [new Claim("sub", sub), new Claim("email", email)],
            expires: DateTime.UtcNow.AddHours(12), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>Creates the account (and a default email alert to its owner) the first time a user calls the API.</summary>
public sealed partial class AccountProvisioner(IAccountRepository accounts, IAlertRepository alerts, TimeProvider clock)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Account> Known = new();

    public async Task<Account> EnsureAsync(string userId, string email, CancellationToken ct)
    {
        if (Known.TryGetValue(userId, out var cached)) return cached;
        var account = await accounts.GetAsync(userId, ct);
        if (account is null)
        {
            var baseSlug = SlugRules.FromEmail(email);
            for (var attempt = 0; attempt < 6 && account is null; attempt++)
            {
                var slug = attempt == 0 ? baseSlug : $"{baseSlug}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
                var candidate = new Account(userId, email, slug, clock.GetUtcNow());
                if (await accounts.CreateAsync(candidate, ct)) account = candidate;
                else if ((await accounts.GetAsync(userId, ct)) is { } existing) account = existing; // lost a race with ourselves
            }
            if (account is null) throw new InvalidOperationException("Could not allocate a status page slug.");
            await alerts.CreateAsync(new AlertSubscription(userId, Guid.NewGuid().ToString("N"), AlertType.Email, email, null, true), ct);
        }
        Known[userId] = account;
        return account;
    }

    public static void Forget(string userId) => Known.TryRemove(userId, out _);
}

public static partial class SlugRules
{
    private static readonly HashSet<string> Reserved = ["api", "status", "admin", "demo", "www", "app", "dev", "health", "login", "static"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,28}[a-z0-9]$")]
    private static partial Regex Valid();

    public static bool IsValid(string? slug) => slug is not null && Valid().IsMatch(slug) && !slug.Contains("--") && !Reserved.Contains(slug);

    public static string FromEmail(string email)
    {
        var local = email.Split('@')[0].ToLowerInvariant();
        var cleaned = new string(local.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        if (cleaned.Length < 3) cleaned = (cleaned + "-user").Trim('-');
        if (cleaned.Length > 24) cleaned = cleaned[..24].Trim('-');
        return IsValid(cleaned) ? cleaned : "user-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
    }
}

public static class HttpContextUser
{
    private const string Key = "statushub.user";
    public static void SetUser(this HttpContext c, CurrentUser u) => c.Items[Key] = u;
    public static CurrentUser GetUser(this HttpContext c) => (CurrentUser)c.Items[Key]!;
}

