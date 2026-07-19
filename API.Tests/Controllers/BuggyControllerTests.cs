using System;                                  // IDisposable, Action
using API.Controllers;                         // BuggyController (derives from BaseApiController)
using API.Errors;                              // ApiResponse (payload of the 404/400 branches)
using FluentAssertions;                        // fluent, diagnostic assertion API (v6.12.0)
using Infrastructure.Data;                     // StoreContext (the concrete DbContext dependency)
using Microsoft.AspNetCore.Mvc;                // ActionResult<T>, NotFoundObjectResult, BadRequestObjectResult, OkResult
using Microsoft.Data.Sqlite;                   // SqliteConnection (in-memory database transport)
using Microsoft.EntityFrameworkCore;           // DbContextOptionsBuilder, UseSqlite, Database.EnsureCreated
using Xunit;                                   // [Fact]

namespace API.Tests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="BuggyController"/>.
    /// <para>
    /// Unlike the other API controllers (which depend on interfaces that can be replaced with
    /// Moq test doubles), <see cref="BuggyController"/> takes the <b>concrete</b>
    /// <see cref="StoreContext"/> in its constructor. Rather than mock the sealed
    /// <c>DbSet</c>/<c>Find</c> surface, these tests build a real <see cref="StoreContext"/>
    /// over an <b>in-memory SQLite</b> database (the provider is available transitively via the
    /// <c>API → Infrastructure</c> project-reference chain, per AAP §0.6.1). The schema is created
    /// with <see cref="RelationalDatabaseFacadeExtensions.EnsureCreated"/> and left <b>empty</b>
    /// (no seed), which is exactly the state the controller's <c>Products.Find(42)</c> lookups
    /// exercise (they return <c>null</c>).
    /// </para>
    /// <para>
    /// An in-memory SQLite database lives only as long as its connection is open, so the
    /// connection is opened in the constructor and held open for the lifetime of the test
    /// instance, then released in <see cref="Dispose"/>. xUnit instantiates a fresh test-class
    /// instance per <c>[Fact]</c>, so every test receives its own isolated, empty database with
    /// no cross-test state — the class is therefore safe to run in parallel.
    /// </para>
    /// <para>
    /// Naming follows the repository convention
    /// <c>MethodName_StateUnderTest_ExpectedBehavior</c>, and each test uses the
    /// Arrange-Act-Assert structure. No production code is modified by anything in this file.
    /// </para>
    /// </summary>
    public class BuggyControllerTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly StoreContext _context;
        private readonly BuggyController _controller;

        /// <summary>
        /// Builds the per-test SQLite in-memory harness: opens the connection, creates the
        /// (empty) schema and wires a real <see cref="BuggyController"/> to the resulting context.
        /// </summary>
        public BuggyControllerTests()
        {
            // Arrange (shared harness): open an in-memory SQLite connection and KEEP IT OPEN for
            // the whole test — closing it would destroy the in-memory database instantly.
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<StoreContext>()
                .UseSqlite(_connection)
                .Options;

            _context = new StoreContext(options);

            // EnsureCreated() materializes the schema (StoreContext.OnModelCreating applies the
            // SQLite value conversions), leaving the tables present but empty (no seed data).
            _context.Database.EnsureCreated();

            _controller = new BuggyController(_context);
        }

        /// <summary>
        /// <c>GetSecretText</c> returns the literal string directly, so the implicit
        /// <see cref="ActionResult{TValue}"/> conversion places it on <c>Value</c> (with a null
        /// <c>Result</c>). The <c>[Authorize]</c> attribute is enforced by the ASP.NET Core
        /// pipeline, not by the method body, so a direct in-process call needs no principal.
        /// </summary>
        [Fact]
        public void GetSecretText_WhenCalled_ReturnsSecretStuffString()
        {
            // Arrange - controller supplied by the fixture constructor.

            // Act
            var result = _controller.GetSecretText();

            // Assert
            result.Value.Should().Be("secret stuff");
            result.Result.Should().BeNull();
        }

        /// <summary>
        /// On an empty database <c>Products.Find(42)</c> returns <c>null</c>, so the controller
        /// takes the not-found branch and returns a <see cref="NotFoundObjectResult"/> whose body
        /// is an <see cref="ApiResponse"/> carrying status code 404 and its default message.
        /// </summary>
        [Fact]
        public void GetNotFoundRequest_WhenProductMissing_ReturnsNotFoundWithApiResponse404()
        {
            // Arrange - database created empty by the fixture (no product with id 42).

            // Act
            var result = _controller.GetNotFoundRequest();

            // Assert
            var notFoundResult = result.Should().BeOfType<NotFoundObjectResult>().Subject;
            var apiResponse = notFoundResult.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(404);
            apiResponse.Message.Should().Be("Resource not found");
        }

        /// <summary>
        /// <c>GetServerError</c> is synchronous: on an empty database <c>Products.Find(42)</c>
        /// returns <c>null</c> and the subsequent <c>thing.ToString()</c> dereference throws a
        /// <see cref="NullReferenceException"/>. The exception is asserted against a synchronous
        /// <see cref="Action"/> (the method is not async, so no awaiting is involved).
        /// </summary>
        [Fact]
        public void GetServerError_WhenProductMissing_ThrowsNullReferenceException()
        {
            // Act
            Action act = () => _controller.GetServerError();

            // Assert
            act.Should().Throw<NullReferenceException>();
        }

        /// <summary>
        /// <c>GetBadRequest</c> always returns a <see cref="BadRequestObjectResult"/> whose body is
        /// an <see cref="ApiResponse"/> carrying status code 400 and its default message.
        /// </summary>
        [Fact]
        public void GetBadRequest_WhenCalled_ReturnsBadRequestWithApiResponse400()
        {
            // Arrange - controller supplied by the fixture constructor.

            // Act
            var result = _controller.GetBadRequest();

            // Assert
            var badRequestResult = result.Should().BeOfType<BadRequestObjectResult>().Subject;
            var apiResponse = badRequestResult.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(400);
            apiResponse.Message.Should().Be("You have made a bad request");
        }

        /// <summary>
        /// The <c>int</c> overload of <c>GetNotFoundRequest</c> (routed at <c>badrequest/{id}</c>)
        /// returns the parameterless <c>Ok()</c>, which yields an <see cref="OkResult"/> — a
        /// bodyless 200 response, distinct from the value-carrying <see cref="OkObjectResult"/>.
        /// </summary>
        [Fact]
        public void GetNotFoundRequestById_WhenCalled_ReturnsOk()
        {
            // Arrange - controller supplied by the fixture constructor.

            // Act
            var result = _controller.GetNotFoundRequest(5);

            // Assert
            result.Should().BeOfType<OkResult>();
        }

        /// <summary>
        /// Tears down the harness by disposing the context first and then the underlying SQLite
        /// connection (which discards the in-memory database).
        /// </summary>
        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }
    }
}
