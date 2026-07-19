using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using API.Errors;
using API.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace API.Tests.Middleware
{
    /// <summary>
    /// Unit tests for the production global exception handler
    /// <see cref="ExceptionMiddleware"/> (namespace <c>API.Middleware</c>).
    /// <para>
    /// <see cref="ExceptionMiddleware"/> is a critical-path component: it is the last line of
    /// defense that converts any unhandled exception in the ASP.NET Core pipeline into a
    /// structured, camelCase <see cref="ApiException"/> JSON payload with HTTP 500. Because a
    /// defect here has the widest blast radius, this suite targets the highest coverage
    /// threshold in the project (&gt;= 90%) and exercises every line and both ternary branches
    /// of <see cref="ExceptionMiddleware.InvokeAsync"/>.
    /// </para>
    /// <para>
    /// The middleware is driven exactly as written — no production code is modified. Each test
    /// follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c> and the
    /// Arrange-Act-Assert structure, and uses FluentAssertions for expressive failure messages.
    /// </para>
    /// </summary>
    public class ExceptionMiddlewareTests
    {
        /// <summary>
        /// Builds a mocked <see cref="IHostEnvironment"/> whose <see cref="IHostEnvironment.EnvironmentName"/>
        /// is set to the supplied value.
        /// <para>
        /// <b>Why mock the property and not <c>IsDevelopment()</c>:</b> the middleware branches on
        /// <c>_env.IsDevelopment()</c>, but <c>IsDevelopment()</c> is a non-virtual <em>extension method</em>
        /// (in <c>Microsoft.Extensions.Hosting</c>) that cannot be intercepted by Moq. Internally it simply
        /// compares <see cref="IHostEnvironment.EnvironmentName"/> against
        /// <see cref="Environments.Development"/> (case-insensitive). Controlling the branch therefore means
        /// stubbing the underlying <c>EnvironmentName</c> property.
        /// </para>
        /// </summary>
        /// <param name="environmentName">
        /// The environment name to expose; prefer the <see cref="Environments"/> constants
        /// (<see cref="Environments.Development"/> / <see cref="Environments.Production"/>).
        /// </param>
        private static Mock<IHostEnvironment> BuildEnvironment(string environmentName)
        {
            var env = new Mock<IHostEnvironment>();
            env.Setup(e => e.EnvironmentName).Returns(environmentName);
            return env;
        }

        /// <summary>
        /// Builds a fresh <see cref="DefaultHttpContext"/> whose response body is backed by a seekable
        /// <see cref="MemoryStream"/> so that anything the middleware writes can be read back and asserted.
        /// A new context (and stream) is created per test to guarantee isolation.
        /// </summary>
        private static DefaultHttpContext BuildHttpContext()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            return context;
        }

        /// <summary>
        /// Rewinds the response body stream and reads its full contents as a string. Callers use this
        /// after invoking the middleware to inspect the serialized error payload.
        /// </summary>
        private static async Task<string> ReadResponseBodyAsync(HttpContext context)
        {
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            return await new StreamReader(context.Response.Body).ReadToEndAsync();
        }

        /// <summary>
        /// Shared, case-insensitive deserialization options. The middleware writes camelCase keys
        /// (<c>statusCode</c>/<c>message</c>/<c>details</c>); enabling
        /// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> lets System.Text.Json bind those
        /// keys onto the <see cref="ApiException"/> constructor parameters regardless of casing.
        /// </summary>
        private static readonly JsonSerializerOptions CaseInsensitive =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Happy path: when the downstream delegate completes without throwing, the middleware must
        /// simply pass the request through — invoking <c>next</c> once and leaving the response status
        /// (200) and body (empty) untouched.
        /// </summary>
        [Fact]
        public async Task InvokeAsync_WhenNoException_CallsNextAndLeavesResponseUnchanged()
        {
            // Arrange
            var nextCalled = false;
            RequestDelegate next = ctx =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            };
            var logger = new Mock<ILogger<ExceptionMiddleware>>();
            var env = BuildEnvironment(Environments.Development);
            var context = BuildHttpContext();
            var middleware = new ExceptionMiddleware(next, logger.Object, env.Object);

            // Act
            await middleware.InvokeAsync(context);

            // Assert
            nextCalled.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.Body.Length.Should().Be(0);
        }

        /// <summary>
        /// Error path (status/content-type): a thrown exception must be caught and converted into an
        /// HTTP 500 response with <c>Content-Type: application/json</c>.
        /// <para>
        /// The <c>next</c> delegate uses a <em>throw expression</em> so that the CLR captures a genuine
        /// stack trace at the throw site; a merely-constructed exception would leave
        /// <see cref="Exception.StackTrace"/> null and the middleware's <c>ex.StackTrace.ToString()</c>
        /// would throw a <see cref="NullReferenceException"/> instead of exercising the real behavior.
        /// </para>
        /// </summary>
        [Fact]
        public async Task InvokeAsync_WhenExceptionThrown_Returns500ApplicationJson()
        {
            // Arrange
            RequestDelegate next = _ => throw new Exception("boom");
            var logger = new Mock<ILogger<ExceptionMiddleware>>();
            var env = BuildEnvironment(Environments.Production);
            var context = BuildHttpContext();
            var middleware = new ExceptionMiddleware(next, logger.Object, env.Object);

            // Act
            await middleware.InvokeAsync(context);

            // Assert
            context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
            context.Response.ContentType.Should().Be("application/json");
        }

        /// <summary>
        /// Development branch (<c>IsDevelopment() == true</c>): the response must surface the real
        /// exception message and a populated stack trace so developers get full diagnostic detail.
        /// </summary>
        [Fact]
        public async Task InvokeAsync_WhenDevelopmentAndExceptionThrown_ResponseIncludesMessageAndStackTrace()
        {
            // Arrange
            RequestDelegate next = _ => throw new Exception("boom");
            var logger = new Mock<ILogger<ExceptionMiddleware>>();
            var env = BuildEnvironment(Environments.Development);
            var context = BuildHttpContext();
            var middleware = new ExceptionMiddleware(next, logger.Object, env.Object);

            // Act
            await middleware.InvokeAsync(context);

            // Assert
            var body = await ReadResponseBodyAsync(context);
            var response = JsonSerializer.Deserialize<ApiException>(body, CaseInsensitive);
            response.Should().NotBeNull();
            response.StatusCode.Should().Be(500);
            response.Message.Should().Be("boom");
            response.Details.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Production branch (<c>IsDevelopment() == false</c>): the response must be suppressed to the
        /// generic <see cref="ApiResponse"/> default message ("Server Error") with no stack-trace details,
        /// so that internal implementation detail never leaks to end users.
        /// </summary>
        [Fact]
        public async Task InvokeAsync_WhenProductionAndExceptionThrown_ResponseSuppressedToGenericMessage()
        {
            // Arrange
            RequestDelegate next = _ => throw new Exception("boom");
            var logger = new Mock<ILogger<ExceptionMiddleware>>();
            var env = BuildEnvironment(Environments.Production);
            var context = BuildHttpContext();
            var middleware = new ExceptionMiddleware(next, logger.Object, env.Object);

            // Act
            await middleware.InvokeAsync(context);

            // Assert
            var body = await ReadResponseBodyAsync(context);
            var response = JsonSerializer.Deserialize<ApiException>(body, CaseInsensitive);
            response.Should().NotBeNull();
            response.StatusCode.Should().Be(500);
            response.Message.Should().Be("Server Error");
            response.Details.Should().BeNull();
        }

        /// <summary>
        /// Observability: every caught exception must be logged at <see cref="LogLevel.Error"/> exactly once.
        /// <para>
        /// The middleware calls the <c>_logger.LogError(ex, ex.Message)</c> extension, which forwards to the
        /// interface method <c>ILogger.Log&lt;TState&gt;(LogLevel, EventId, TState, Exception, Func&lt;TState,Exception,string&gt;)</c>.
        /// Extension methods cannot be verified directly on a Moq mock, so the verification targets the
        /// underlying <c>Log</c> method using the <see cref="It.IsAnyType"/> matcher (Moq &gt;= 4.13) with the
        /// required formatter-delegate cast.
        /// </para>
        /// </summary>
        [Fact]
        public async Task InvokeAsync_WhenExceptionThrown_LogsError()
        {
            // Arrange
            RequestDelegate next = _ => throw new Exception("boom");
            var logger = new Mock<ILogger<ExceptionMiddleware>>();
            var env = BuildEnvironment(Environments.Production);
            var context = BuildHttpContext();
            var middleware = new ExceptionMiddleware(next, logger.Object, env.Object);

            // Act
            await middleware.InvokeAsync(context);

            // Assert
            logger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    (Func<It.IsAnyType, Exception, string>) It.IsAny<object>()),
                Times.Once);
        }

        /// <summary>
        /// Serialization contract: the error payload must use camelCase property names
        /// (<c>statusCode</c>/<c>message</c>) — never the PascalCase CLR names — because the middleware
        /// configures <see cref="JsonNamingPolicy.CamelCase"/>. This guards the wire contract that clients
        /// depend upon.
        /// </summary>
        [Fact]
        public async Task InvokeAsync_WhenExceptionThrown_SerializesResponseUsingCamelCase()
        {
            // Arrange
            RequestDelegate next = _ => throw new Exception("boom");
            var logger = new Mock<ILogger<ExceptionMiddleware>>();
            var env = BuildEnvironment(Environments.Production);
            var context = BuildHttpContext();
            var middleware = new ExceptionMiddleware(next, logger.Object, env.Object);

            // Act
            await middleware.InvokeAsync(context);

            // Assert
            var body = await ReadResponseBodyAsync(context);
            body.Should().Contain("\"statusCode\"").And.Contain("\"message\"");
            body.Should().NotContain("\"StatusCode\"").And.NotContain("\"Message\"");
        }
    }
}
