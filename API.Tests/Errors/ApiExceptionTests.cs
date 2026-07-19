using API.Errors;
using FluentAssertions;
using Xunit;

namespace API.Tests.Errors
{
    public class ApiExceptionTests
    {
        [Fact]
        public void Constructor_WhenDetailsProvided_SetsStatusCodeMessageAndDetails()
        {
            // Act
            var apiException = new ApiException(500, "msg", "stacktrace-or-detail");

            // Assert
            apiException.StatusCode.Should().Be(500);
            apiException.Message.Should().Be("msg");
            apiException.Details.Should().Be("stacktrace-or-detail");
        }

        [Fact]
        public void Constructor_WhenOnlyStatusCodeProvided_InheritsDefaultMessageAndNullDetails()
        {
            // Act
            var apiException = new ApiException(404);

            // Assert
            apiException.StatusCode.Should().Be(404);
            apiException.Message.Should().Be("Resource not found");
            apiException.Details.Should().BeNull();
        }

        [Fact]
        public void Constructor_WhenDetailsOmitted_DetailsIsNull()
        {
            // Act
            var apiException = new ApiException(500);

            // Assert
            apiException.StatusCode.Should().Be(500);
            apiException.Message.Should().Be("Server Error");
            apiException.Details.Should().BeNull();
        }
    }
}
