using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
// EF Core 5.0 ships the async-query contract IAsyncQueryProvider in the
// Microsoft.EntityFrameworkCore.Query namespace (it moved out of the
// *.Query.Internal namespace after EF Core 2.x). This assembly is available
// transitively via the API -> Infrastructure project reference chain.
using Microsoft.EntityFrameworkCore.Query;
using Moq;

namespace API.Tests.Helpers
{
    /// <summary>
    /// Shared, test-support utilities for the <c>API.Tests</c> unit-test project.
    /// <para>
    /// This class is intentionally <b>support-only</b>: it contains no
    /// <c>[Fact]</c>/<c>[Theory]</c> methods and performs no assertions. It centralizes the
    /// identity/authentication mock construction (per AAP §0.4.4) so that every controller
    /// test in the sibling <c>API.Tests/Controllers/</c> folder reuses a single, canonical
    /// implementation instead of re-deriving the (verbose) ASP.NET Core Identity mocking
    /// boilerplate.
    /// </para>
    /// <para>
    /// It is declared <c>static</c> so it can host the <see cref="WithUser{TController}"/>
    /// extension method. No production code is modified by anything in this file.
    /// </para>
    /// </summary>
    public static class ControllerTestHelpers
    {
        // ------------------------------------------------------------------
        // Identity manager mocks (standard IUserStore pattern, AAP §0.4.4)
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a <see cref="Mock{T}"/> of <see cref="UserManager{AppUser}"/> for the
        /// application's <see cref="AppUser"/> type. Convenience overload delegating to
        /// <see cref="GetMockUserManager{TUser}"/>.
        /// </summary>
        /// <returns>A configurable <see cref="Mock{UserManager}"/> whose <c>Object</c> is usable
        /// wherever a real <see cref="UserManager{AppUser}"/> is required.</returns>
        public static Mock<UserManager<AppUser>> GetMockUserManager()
            => GetMockUserManager<AppUser>();

        /// <summary>
        /// Creates a <see cref="Mock{T}"/> of <see cref="UserManager{TUser}"/> using the standard
        /// ASP.NET Core Identity <see cref="IUserStore{TUser}"/> construction pattern.
        /// </summary>
        /// <remarks>
        /// <see cref="UserManager{TUser}"/> exposes a single public constructor with nine
        /// parameters:
        /// (<see cref="IUserStore{TUser}"/>, <c>IOptions&lt;IdentityOptions&gt;</c>,
        /// <c>IPasswordHasher&lt;TUser&gt;</c>, <c>IEnumerable&lt;IUserValidator&lt;TUser&gt;&gt;</c>,
        /// <c>IEnumerable&lt;IPasswordValidator&lt;TUser&gt;&gt;</c>, <c>ILookupNormalizer</c>,
        /// <c>IdentityErrorDescriber</c>, <c>IServiceProvider</c>, <c>ILogger&lt;UserManager&lt;TUser&gt;&gt;</c>).
        /// Only the store must be supplied for the manager to construct; the remaining eight
        /// arguments are passed as <c>null</c>. The manager's <c>Users</c>, <c>FindByEmailAsync</c>,
        /// <c>CreateAsync</c>, <c>UpdateAsync</c>, etc. members are <c>virtual</c>, so callers can
        /// override them via <c>Setup(...)</c>.
        /// </remarks>
        /// <typeparam name="TUser">The identity user type (reference type).</typeparam>
        /// <returns>A configurable <see cref="Mock{UserManager}"/>.</returns>
        public static Mock<UserManager<TUser>> GetMockUserManager<TUser>() where TUser : class
        {
            var store = new Mock<IUserStore<TUser>>();

            // 9-arg UserManager<TUser> ctor => store.Object followed by 8 nulls.
            // Do NOT change the argument count: Moq must be able to bind a reachable ctor.
            return new Mock<UserManager<TUser>>(
                store.Object, null, null, null, null, null, null, null, null);
        }

        /// <summary>
        /// Creates a <see cref="Mock{T}"/> of <see cref="SignInManager{AppUser}"/> wired to the
        /// supplied (or a freshly created) mocked <see cref="UserManager{AppUser}"/>.
        /// </summary>
        /// <remarks>
        /// The ASP.NET Core 5.0 <see cref="SignInManager{TUser}"/> exposes a seven-parameter
        /// constructor:
        /// (<see cref="UserManager{TUser}"/>, <see cref="IHttpContextAccessor"/>,
        /// <c>IUserClaimsPrincipalFactory&lt;TUser&gt;</c>, <c>IOptions&lt;IdentityOptions&gt;</c>,
        /// <c>ILogger&lt;SignInManager&lt;TUser&gt;&gt;</c>, <c>IAuthenticationSchemeProvider</c>,
        /// <c>IUserConfirmation&lt;TUser&gt;</c>). The first three are supplied with real doubles so
        /// the manager can construct; the remaining four are <c>null</c>. Members such as
        /// <c>CheckPasswordSignInAsync</c> are <c>virtual</c> and can be overridden with
        /// <c>Setup(...)</c>.
        /// </remarks>
        /// <param name="userManager">An existing mocked user manager to wrap. When <c>null</c>, a
        /// fresh mock is created via <see cref="GetMockUserManager()"/>, allowing standalone use.</param>
        /// <returns>A configurable <see cref="Mock{SignInManager}"/>.</returns>
        public static Mock<SignInManager<AppUser>> GetMockSignInManager(
            Mock<UserManager<AppUser>> userManager = null)
        {
            userManager ??= GetMockUserManager();

            // 7-arg SignInManager<AppUser> ctor (ASP.NET Core 5.0):
            // userManager + IHttpContextAccessor + IUserClaimsPrincipalFactory, then 4 nulls.
            return new Mock<SignInManager<AppUser>>(
                userManager.Object,
                Mock.Of<IHttpContextAccessor>(),
                Mock.Of<IUserClaimsPrincipalFactory<AppUser>>(),
                null, null, null, null);
        }

        // ------------------------------------------------------------------
        // ClaimsPrincipal builder
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds an authenticated <see cref="ClaimsPrincipal"/> carrying the email claim the
        /// application reads (<see cref="ClaimTypes.Email"/>). This mirrors the claims the JWT
        /// pipeline populates so controller tests can exercise the
        /// <c>RetrieveEmailFromPrincipal</c>/<c>FindByEmailFromClaimsPrinciple</c> paths.
        /// </summary>
        /// <remarks>
        /// A non-null <c>authenticationType</c> is passed to <see cref="ClaimsIdentity"/> so that
        /// <c>principal.Identity.IsAuthenticated</c> evaluates to <c>true</c> — required to
        /// simulate an authenticated caller for <c>[Authorize]</c>-gated actions.
        /// </remarks>
        /// <param name="email">The email address to expose as <see cref="ClaimTypes.Email"/>.</param>
        /// <param name="displayName">Optional display name; when provided it is added as both
        /// <see cref="ClaimTypes.GivenName"/> and <see cref="ClaimTypes.Name"/>.</param>
        /// <returns>An authenticated <see cref="ClaimsPrincipal"/>.</returns>
        public static ClaimsPrincipal GetClaimsPrincipal(string email, string displayName = null)
        {
            var claims = new List<Claim> { new Claim(ClaimTypes.Email, email) };

            if (!string.IsNullOrEmpty(displayName))
            {
                claims.Add(new Claim(ClaimTypes.GivenName, displayName));
                claims.Add(new Claim(ClaimTypes.Name, displayName));
            }

            // A non-null authenticationType makes Identity.IsAuthenticated == true.
            var identity = new ClaimsIdentity(claims, "TestAuthentication");
            return new ClaimsPrincipal(identity);
        }

        // ------------------------------------------------------------------
        // Controller <-> principal wiring (authenticated vs unauthenticated)
        // ------------------------------------------------------------------

        /// <summary>
        /// Fluent extension that attaches the supplied <see cref="ClaimsPrincipal"/> to a
        /// controller by assigning a fresh <see cref="ControllerContext"/> backed by a
        /// <see cref="DefaultHttpContext"/>. This is how a controller's
        /// <c>HttpContext.User</c> is set in isolation (there is no request pipeline in a unit test).
        /// </summary>
        /// <typeparam name="TController">A controller deriving from <see cref="ControllerBase"/>.</typeparam>
        /// <param name="controller">The controller under test.</param>
        /// <param name="user">The principal to attach. Passing an anonymous/empty principal
        /// simulates an unauthenticated caller.</param>
        /// <returns>The same <paramref name="controller"/> instance, enabling call chaining.</returns>
        public static TController WithUser<TController>(this TController controller, ClaimsPrincipal user)
            where TController : ControllerBase
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            };

            return controller;
        }

        /// <summary>
        /// Non-fluent overload of <see cref="WithUser{TController}"/> for call sites that prefer a
        /// statement-style API. Delegates to the fluent extension.
        /// </summary>
        /// <param name="controller">The controller under test.</param>
        /// <param name="user">The principal to attach.</param>
        public static void SetUser(ControllerBase controller, ClaimsPrincipal user)
            => controller.WithUser(user);

        // ------------------------------------------------------------------
        // Async-queryable UserManager.Users setup
        // ------------------------------------------------------------------

        /// <summary>
        /// Wires <c>mockUserManager.Users</c> to an async-capable <see cref="IQueryable{AppUser}"/>
        /// so that EF Core async operators (e.g. <c>SingleOrDefaultAsync</c>, <c>Include(...)</c>)
        /// invoked by <c>UserManagerExtensions</c> execute against the in-memory sequence without
        /// throwing "The provider for the source 'IQueryable' doesn't implement 'IAsyncQueryProvider'".
        /// </summary>
        /// <remarks>
        /// <see cref="UserManager{TUser}"/>.<c>Users</c> is <c>virtual</c>, so Moq can override it.
        /// The backing <see cref="TestAsyncEnumerable{T}"/> supplies the async query provider.
        /// </remarks>
        /// <param name="mockUserManager">The mocked user manager to configure.</param>
        /// <param name="users">The set of users the mocked <c>Users</c> property should expose.</param>
        public static void MockUserManagerUsers(
            Mock<UserManager<AppUser>> mockUserManager, IEnumerable<AppUser> users)
        {
            var data = new TestAsyncEnumerable<AppUser>(users.AsQueryable());
            mockUserManager.Setup(m => m.Users).Returns(data);
        }
    }

    // ======================================================================
    // EF Core 3.0+/5.0 async-query test doubles (official Microsoft docs pattern).
    // These enable LINQ-to-Objects sequences to satisfy EF Core's async LINQ
    // operators when a repository/manager is exercised without a real database.
    // ======================================================================

    /// <summary>
    /// An <see cref="IAsyncQueryProvider"/> that forwards synchronous execution to an inner
    /// LINQ-to-Objects provider and adapts it to EF Core's asynchronous execution contract.
    /// </summary>
    /// <typeparam name="TEntity">The element type of the query.</typeparam>
    internal class TestAsyncQueryProvider<TEntity> : IAsyncQueryProvider
    {
        private readonly IQueryProvider _inner;

        internal TestAsyncQueryProvider(IQueryProvider inner) => _inner = inner;

        public IQueryable CreateQuery(Expression expression)
            => new TestAsyncEnumerable<TEntity>(expression);

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
            => new TestAsyncEnumerable<TElement>(expression);

        public object Execute(Expression expression) => _inner.Execute(expression);

        public TResult Execute<TResult>(Expression expression) => _inner.Execute<TResult>(expression);

        /// <summary>
        /// Executes the expression synchronously via the inner provider and wraps the result in a
        /// completed <see cref="Task{TResult}"/> so that EF Core's async operators (whose
        /// <c>TResult</c> is a <see cref="Task{T}"/>) receive a valid awaitable.
        /// </summary>
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var expectedResultType = typeof(TResult).GetGenericArguments()[0];
            var executionResult = ((IQueryProvider)this).Execute(expression);

            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))
                .MakeGenericMethod(expectedResultType)
                .Invoke(null, new[] { executionResult });
        }
    }

    /// <summary>
    /// An <see cref="EnumerableQuery{T}"/> that also implements <see cref="IAsyncEnumerable{T}"/> and
    /// exposes a <see cref="TestAsyncQueryProvider{TEntity}"/> so it can back an async EF Core query.
    /// </summary>
    /// <typeparam name="T">The element type of the sequence.</typeparam>
    internal class TestAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        public TestAsyncEnumerable(IEnumerable<T> enumerable) : base(enumerable) { }

        public TestAsyncEnumerable(Expression expression) : base(expression) { }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new TestAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());

        IQueryProvider IQueryable.Provider => new TestAsyncQueryProvider<T>(this);
    }

    /// <summary>
    /// An <see cref="IAsyncEnumerator{T}"/> adapter over a synchronous <see cref="IEnumerator{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type of the sequence.</typeparam>
    internal class TestAsyncEnumerator<T> : IAsyncEnumerator<T>
    {
        private readonly IEnumerator<T> _inner;

        public TestAsyncEnumerator(IEnumerator<T> inner) => _inner = inner;

        public T Current => _inner.Current;

        public ValueTask<bool> MoveNextAsync() => new ValueTask<bool>(_inner.MoveNext());

        public ValueTask DisposeAsync()
        {
            _inner.Dispose();
            return new ValueTask();
        }
    }
}
