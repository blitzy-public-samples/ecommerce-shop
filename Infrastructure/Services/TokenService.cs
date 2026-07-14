using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Core.Entities.Identity;
using Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Services
{
    public class TokenService : ITokenService
    {
        private readonly IConfiguration _config;
        private readonly SymmetricSecurityKey _key;

        public TokenService(IConfiguration config)
        {
            _config = config;
            // Derive a 512-bit signing key from the configured secret via SHA-512. HMAC-SHA512
            // (used in CreateToken below) requires a key of at least 512 bits under
            // Microsoft.IdentityModel 8.x, which enforces per-algorithm key sizes (added in 6.30.1);
            // the configured Token:Key is shorter, so it is expanded here deterministically rather
            // than altering the immutable configuration value. The HMAC-SHA512 algorithm and the
            // emitted token shape (claims, expiry, issuer) are preserved unchanged.
            _key = new SymmetricSecurityKey(SHA512.HashData(Encoding.UTF8.GetBytes(_config["Token:Key"])));
        }

        public string CreateToken(AppUser user)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.GivenName, user.DisplayName)
            };

            var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha512Signature);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.Now.AddDays(7),
                SigningCredentials = creds,
                Issuer = _config["Token:Issuer"]
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}