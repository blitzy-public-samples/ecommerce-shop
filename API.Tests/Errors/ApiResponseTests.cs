using API.Errors;
using FluentAssertions;
using Xunit;

namespace API.Tests.Errors
{
    public class ApiResponseTests
    {
        [Theory]
        [InlineData(400, "You have made a bad request")]
        [InlineData(401, "You are not authorized")]
        [InlineData(404, "Resource not found")]
        [InlineData(500, "Server Error")]
        public void Constructor_WhenKnownStatusCodeAndNoMessage_SetsDefaultMessage(
            int statusCode, string expectedMessage)
        {
            // Act
            var response = new ApiResponse(statusCode);

            // Assert
            response.StatusCode.Should().Be(statusCode);
            response.Message.Should().Be(expectedMessage);
        }

        [Fact]
        public void Constructor_WhenCustomMessageProvided_UsesProvidedMessage()
        {
            // Arrange
            const string customMessage = "Custom failure message";

            // Act
            var response = new ApiResponse(400, customMessage);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Message.Should().Be(customMessage);
        }

        [Theory]
        [InlineData(200)]
        [InlineData(418)]
        [InlineData(999)]
        public void Constructor_WhenUnknownStatusCodeAndNoMessage_MessageIsNull(int statusCode)
        {
            // Act
            var response = new ApiResponse(statusCode);

            // Assert
            response.StatusCode.Should().Be(statusCode);
            response.Message.Should().BeNull();
        }
    }
}
