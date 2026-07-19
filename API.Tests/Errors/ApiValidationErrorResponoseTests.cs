using System.Collections.Generic;
using API.Errors;
using FluentAssertions;
using Xunit;

namespace API.Tests.Errors
{
    public class ApiValidationErrorResponoseTests
    {
        [Fact]
        public void Constructor_WhenCreated_SetsStatusCode400WithDefaultBadRequestMessage()
        {
            // Act
            var response = new ApiValidationErrorResponose();

            // Assert
            response.StatusCode.Should().Be(400);
            response.Message.Should().Be("You have made a bad request");
        }

        [Fact]
        public void Errors_WhenAssigned_RoundTripsCollection()
        {
            // Arrange
            var errors = new[] { "e1", "e2" };

            // Act
            var response = new ApiValidationErrorResponose { Errors = errors };

            // Assert
            response.Errors.Should().Equal("e1", "e2");
            response.Errors.Should().HaveCount(2);
        }

        [Fact]
        public void Errors_WhenNotAssigned_IsNullByDefault()
        {
            // Act
            var response = new ApiValidationErrorResponose();

            // Assert
            response.Errors.Should().BeNull();
        }
    }
}
