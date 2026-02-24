using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Mimir.Api.Services;

public class TokenService
{
    private readonly SymmetricSecurityKey _key;

    public TokenService(IConfiguration config)
    {
        var secret = config["JWT_SECRET"]
            ?? throw new InvalidOperationException("JWT_SECRET environment variable is not set.");
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public string CreateToken(int userNo, bool isAdmin)
    {
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim("userNo", userNo.ToString()),
        };

        if (isAdmin)
            claims.Add(new Claim("role", "admin"));

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
