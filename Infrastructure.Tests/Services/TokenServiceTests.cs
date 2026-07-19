using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Core.Entities.Identity;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Infrastructure.Tests.Services
{
    public class TokenServiceTests
    {
        // 64-char ASCII key => 64 bytes, well above the 128-bit (16-byte) HMAC-SHA512 minimum.
        private const string ValidKey =
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        private const string Issuer = "https://localhost";

        private static Mock<IConfiguration> BuildConfig(string key, string issuer)
        {
            var config = new Mock<IConfiguration>();
            config.Setup(c => c["Token:Key"]).Returns(key);
            config.Setup(c => c["Token:Issuer"]).Returns(issuer);
            return config;
        }

        private static AppUser SampleUser() =>
            new AppUser { Email = "bob@test.com", DisplayName = "Bob" };

        [Fact]
        public void CreateToken_WhenUserValid_EmbedsEmailGivenNameAndIssuerSignedWithHs512()
        {
            // Arrange
            var config = BuildConfig(ValidKey, Issuer);
            var sut = new TokenService(config.Object);
            var user = SampleUser();

            // Act
            var tokenString = sut.CreateToken(user);

            // Assert
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenString);

            // Outbound claim-type mapping renames ClaimTypes.Email -> "email" (JwtRegisteredClaimNames.Email)
            // and ClaimTypes.GivenName -> "given_name" (JwtRegisteredClaimNames.GivenName) in the written token.
            jwt.Claims.Should().Contain(c =>
                c.Type == JwtRegisteredClaimNames.Email && c.Value == user.Email);
            jwt.Claims.Should().Contain(c =>
                c.Type == JwtRegisteredClaimNames.GivenName && c.Value == user.DisplayName);
            jwt.Issuer.Should().Be(Issuer);

            // HmacSha512Signature (xmldsig URI) is mapped to the short JWT alg name "HS512".
            jwt.Header.Alg.Should().BeOneOf(
                SecurityAlgorithms.HmacSha512, SecurityAlgorithms.HmacSha512Signature);
        }

        [Fact]
        public void CreateToken_WhenUserValid_SetsExpiryApproximatelySevenDaysFromNow()
        {
            // Arrange
            var config = BuildConfig(ValidKey, Issuer);
            var sut = new TokenService(config.Object);
            var user = SampleUser();

            // Act
            var tokenString = sut.CreateToken(user);

            // Assert
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenString);
            jwt.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(5));
        }

        [Fact]
        public void CreateToken_WhenKeyTooShortForHmacSha512_Throws()
        {
            // Arrange - "short" = 5 bytes = 40 bits, below the 128-bit HMAC-SHA512 minimum.
            // The SymmetricSecurityKey is constructed fine; the key-size validation (IDX10653)
            // fires at signing time inside CreateToken, throwing ArgumentOutOfRangeException.
            var config = BuildConfig("short", Issuer);
            var sut = new TokenService(config.Object);
            var user = SampleUser();

            // Act
            Action act = () => sut.CreateToken(user);

            // Assert - ArgumentOutOfRangeException derives from ArgumentException; assert the base
            // type so the test is robust to the exact runtime exception subtype.
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Constructor_WhenTokenKeyNull_ThrowsArgumentNullException()
        {
            // Arrange - Token:Key is left unset, so the Moq IConfiguration indexer returns null.
            // Encoding.UTF8.GetBytes((string)null) throws ArgumentNullException in the constructor.
            var config = new Mock<IConfiguration>();
            config.Setup(c => c["Token:Issuer"]).Returns(Issuer);

            // Act
            Action act = () => new TokenService(config.Object);

            // Assert
            act.Should().Throw<ArgumentNullException>();
        }
    }
}
