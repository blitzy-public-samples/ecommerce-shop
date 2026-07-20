using API.Controllers;
using API.Errors;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;   // ObjectResult
using Xunit;

namespace API.Tests.Controllers
{
    /// <summary>
    /// Pure unit tests (no mocking, no I/O) for <see cref="ErrorController"/>.
    /// <para>
    /// <see cref="ErrorController"/> is the simplest controller in the API layer: it has
    /// no constructor and no injected dependencies, and exposes a single action
    /// <c>Error(int code)</c> that wraps an <see cref="ApiResponse"/> in a bare
    /// <see cref="ObjectResult"/> (see <c>API/Controllers/ErrorController.cs</c>).
    /// </para>
    /// <para>
    /// The tests focus on three concerns:
    /// (1) that documented status codes (400/401/404/500) produce an
    /// <see cref="ObjectResult"/> whose payload is an <see cref="ApiResponse"/> carrying the
    /// matching <see cref="ApiResponse.StatusCode"/> and the framework-derived default
    /// <see cref="ApiResponse.Message"/>;
    /// (2) that an unknown status code exercises the <c>ApiResponse</c> switch's default
    /// branch and yields a <c>null</c> message; and
    /// (3) the result-type nuance that the action returns a <em>bare</em>
    /// <see cref="ObjectResult"/>, so <see cref="ObjectResult.StatusCode"/> is never
    /// assigned (the numeric code lives only on the wrapped <see cref="ApiResponse"/>).
    /// </para>
    /// <para>
    /// Each test constructs a fresh <see cref="ErrorController"/>, so the class carries no
    /// shared mutable state and is safe to run in parallel. Tests follow the
    /// <c>MethodName_StateUnderTest_ExpectedBehavior</c> naming convention and use
    /// FluentAssertions for expressive, diagnostic failure messages.
    /// </para>
    /// </summary>
    public class ErrorControllerTests
    {
        /// <summary>
        /// Happy path: for every documented HTTP status code, the action returns an
        /// <see cref="ObjectResult"/> whose value is an <see cref="ApiResponse"/> with the
        /// matching status code and the default message derived by <see cref="ApiResponse"/>.
        /// </summary>
        [Theory]
        [InlineData(400, "You have made a bad request")]
        [InlineData(401, "You are not authorized")]
        [InlineData(404, "Resource not found")]
        [InlineData(500, "Server Error")]
        public void Error_WithDocumentedStatusCode_ReturnsObjectResultWithMatchingApiResponse(
            int code, string expectedMessage)
        {
            // Arrange
            var controller = new ErrorController();

            // Act
            var result = controller.Error(code);

            // Assert
            result.Should().BeOfType<ObjectResult>();
            var objectResult = result as ObjectResult;

            objectResult.Value.Should().BeOfType<ApiResponse>();
            var apiResponse = objectResult.Value as ApiResponse;

            apiResponse.StatusCode.Should().Be(code);
            apiResponse.Message.Should().Be(expectedMessage);
        }

        /// <summary>
        /// Edge case: an unknown/undocumented status code exercises the default branch of the
        /// <see cref="ApiResponse"/> message switch, producing an <see cref="ApiResponse"/> that
        /// echoes the supplied code with a <c>null</c> message.
        /// </summary>
        [Fact]
        public void Error_WithUnknownStatusCode_ReturnsApiResponseWithNullMessage()
        {
            // Arrange
            var controller = new ErrorController();

            // Act
            var result = controller.Error(999);

            // Assert
            result.Should().BeOfType<ObjectResult>();
            var objectResult = result as ObjectResult;

            objectResult.Value.Should().BeOfType<ApiResponse>();
            var apiResponse = objectResult.Value as ApiResponse;

            apiResponse.StatusCode.Should().Be(999);
            apiResponse.Message.Should().BeNull();
        }

        /// <summary>
        /// Result-type nuance: the action returns a <em>bare</em> <see cref="ObjectResult"/>
        /// (it never assigns <see cref="ObjectResult.StatusCode"/>). This documents that the
        /// numeric code is carried by the wrapped <see cref="ApiResponse"/> payload rather than
        /// by the HTTP result wrapper.
        /// </summary>
        [Fact]
        public void Error_WhenCalled_DoesNotSetObjectResultStatusCode()
        {
            // Arrange
            var controller = new ErrorController();

            // Act
            var result = controller.Error(404);

            // Assert
            var objectResult = result as ObjectResult;
            objectResult.Should().NotBeNull();
            objectResult.StatusCode.Should().BeNull();
        }
    }
}
