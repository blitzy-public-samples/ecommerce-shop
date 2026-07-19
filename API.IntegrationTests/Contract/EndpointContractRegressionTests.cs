using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;   // net5.0 shared-framework extension: PostAsJsonAsync / ReadFromJsonAsync
using System.Text.Json;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace API.IntegrationTests.Contract
{
    /// <summary>
    /// HTTP <b>contract-regression</b> integration tests. This class is the contract-stability safety net
    /// for the whole API surface (AAP §0.1.1 Part B, §0.4.1, §0.5.1/§0.5.2): it drives the <b>real</b>
    /// ASP.NET Core application in-process — through the shared <see cref="CustomWebApplicationFactory"/>
    /// over genuine <see cref="HttpClient"/>s — against <b>real</b> PostgreSQL and Redis provisioned by
    /// Testcontainers (via the shared <see cref="ContainerFixture"/>) with the documented seed data, and
    /// asserts the <b>status code</b> plus the <b>serialized JSON DTO shape</b> for <b>every documented
    /// endpoint</b> across <c>API/Controllers/*</c>. If any controller's route, HTTP status code, or
    /// serialized property names/casing change, a test here fails.
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing is mocked or faked here — PostgreSQL, Redis
    /// and the HTTP transport are all genuine. Stripe-touching routes go through the harness's already
    /// registered offline <see cref="StripePaymentServiceStub"/>; no live Stripe call is ever made and no
    /// webhook is signed in this file. The shared <c>docker-compose</c> stack is never touched — the
    /// <see cref="ContainerFixture"/> owns the isolated, disposable containers. There is no
    /// <c>Thread.Sleep</c>: readiness is guaranteed by the fixture's Testcontainers wait strategies, and
    /// every asynchronous call is <c>await</c>ed directly.
    /// </para>
    ///
    /// <para>
    /// <b>Shared containers, one set per collection.</b> The class is annotated
    /// <c>[Collection("Integration")]</c> and receives the shared <see cref="ContainerFixture"/> through its
    /// constructor, so it joins the single set of containers started once and disposed once for the whole
    /// integration suite (never a destructive test — this class only reads/echoes, never stops a container).
    /// Any state created in the shared containers is uniquely keyed with a <see cref="Guid"/> (basket ids,
    /// registration e-mails) so it can never collide with sibling classes.
    /// </para>
    ///
    /// <para>
    /// <b>Assertion strategy.</b> Bodies are inspected with <see cref="JsonDocument"/>/<see cref="JsonElement"/>
    /// rather than deserialized into production DTOs. This keeps the tests decoupled from the production
    /// types while asserting the exact wire contract: the application serializes with the
    /// <see cref="System.Text.Json"/> default camelCase policy (MVC <c>AddControllers()</c>, the
    /// <c>ExceptionMiddleware</c> handler, and the Redis <c>ResponseCacheService</c> all emit camelCase), so
    /// property presence is verified using camelCase names (<c>pageIndex</c>, <c>statusCode</c>,
    /// <c>pictureUrl</c>, <c>productBrand</c>, <c>shortName</c>, <c>paymentIntentId</c>, <c>clientSecret</c>,
    /// …). Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c> with an
    /// Arrange-Act-Assert structure and FluentAssertions.
    /// </para>
    /// </summary>
    [Collection("Integration")]
    public class EndpointContractRegressionTests
    {
        /// <summary>Shared, already-started PostgreSQL + Redis + in-process host harness (injected by xUnit).</summary>
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// Receives the shared <see cref="ContainerFixture"/> for the <c>"Integration"</c> collection. xUnit
        /// constructs one fixture for the whole collection and injects the same instance into every class
        /// annotated <c>[Collection("Integration")]</c>.
        /// </summary>
        /// <param name="fixture">The shared container/host fixture.</param>
        public EndpointContractRegressionTests(ContainerFixture fixture)
        {
            _fixture = fixture;
        }

        // ---------------------------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the response body and returns its root <see cref="JsonElement"/>. The element is
        /// <see cref="JsonElement.Clone">cloned</see> so it remains valid after the backing
        /// <see cref="JsonDocument"/> is disposed. Reading via the response stream avoids materialising the
        /// whole body as a string and works uniformly for JSON objects, arrays and literals (e.g. the
        /// <c>emailexists</c> boolean).
        /// </summary>
        /// <param name="response">The HTTP response whose JSON body is inspected.</param>
        /// <returns>The cloned root JSON element.</returns>
        private static async Task<JsonElement> ReadRootAsync(HttpResponseMessage response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.Clone(); // Clone so it survives after the doc is disposed.
        }

        /// <summary>
        /// Asserts that <paramref name="element"/> is a JSON object exposing every one of the supplied
        /// <b>camelCase</b> property names. This is the core wire-contract check: it fails if a serialized
        /// property is renamed, removed, or its casing changes.
        /// </summary>
        /// <param name="element">The JSON element expected to be an object.</param>
        /// <param name="propertyNames">The camelCase property names that must be present.</param>
        private static void ShouldExposeCamelCaseProperties(JsonElement element, params string[] propertyNames)
        {
            element.ValueKind.Should().Be(JsonValueKind.Object, "the response body must be a JSON object");
            foreach (var name in propertyNames)
            {
                element.TryGetProperty(name, out _).Should()
                    .BeTrue($"the serialized contract must expose the camelCase property '{name}'");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 1 — Products endpoints (anonymous client)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET api/products</c> returns <c>200 OK</c> wrapping the pagination envelope with the seeded
        /// defaults: page 1, page size 6, total count 18, and a 6-element <c>data</c> array whose items are
        /// <c>ProductToReturnDto</c>-shaped.
        /// </summary>
        [Fact]
        public async Task GetProducts_DefaultParams_Returns200WithPaginationEnvelope()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/products");

            // Assert — status + pagination envelope contract (camelCase).
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "pageIndex", "pageSize", "count", "data");
            root.GetProperty("pageIndex").GetInt32().Should().Be(1);
            root.GetProperty("pageSize").GetInt32().Should().Be(6);
            root.GetProperty("count").GetInt32().Should().Be(18);

            var data = root.GetProperty("data");
            data.ValueKind.Should().Be(JsonValueKind.Array);
            data.GetArrayLength().Should().Be(6);

            // The item shape is the mapped ProductToReturnDto (productType/productBrand are flattened strings).
            ShouldExposeCamelCaseProperties(
                data[0], "id", "name", "description", "price", "pictureUrl", "productType", "productBrand");
        }

        /// <summary>
        /// <c>GET api/products/{id}</c> returns <c>200 OK</c> with a <c>ProductToReturnDto</c> whose <c>id</c>
        /// equals the requested id. The id is derived dynamically from the first item of
        /// <c>GET api/products</c> because product ids are database-assigned (products.json has no explicit
        /// id), so hardcoding one would be brittle.
        /// </summary>
        [Fact]
        public async Task GetProduct_ExistingId_Returns200WithProductDto()
        {
            // Arrange — discover a real, seeded product id dynamically (ids are DB-assigned, never hardcoded).
            var client = _fixture.CreateClient();
            var listRoot = await ReadRootAsync(await client.GetAsync("api/products"));
            var existingId = listRoot.GetProperty("data")[0].GetProperty("id").GetInt32();

            // Act
            var response = await client.GetAsync($"api/products/{existingId}");

            // Assert — status + single-product DTO shape + id echo.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(
                root, "id", "name", "description", "price", "pictureUrl", "productType", "productBrand");
            root.GetProperty("id").GetInt32().Should().Be(existingId);
        }

        /// <summary>
        /// <c>GET api/products/9999</c> (an id absent among the 18 seeded products) returns
        /// <c>404 NotFound</c> with the structured <c>ApiResponse</c> body: <c>statusCode == 404</c> and the
        /// default non-empty message "Resource not found".
        /// </summary>
        [Fact]
        public async Task GetProduct_NonexistentId_Returns404ApiResponse()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/products/9999");

            // Assert — status + ApiResponse contract.
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message");
            root.GetProperty("statusCode").GetInt32().Should().Be(404);
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }

        /// <summary>
        /// <c>GET api/products/brands</c> returns <c>200 OK</c> with a JSON array of the 6 seeded brands,
        /// each exposing the camelCase <c>id</c> and <c>name</c> of <c>ProductBrand</c>.
        /// </summary>
        [Fact]
        public async Task GetProductBrands_Seeded_Returns200With6Brands()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/products/brands");

            // Assert — status + array length + element shape.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.ValueKind.Should().Be(JsonValueKind.Array);
            root.GetArrayLength().Should().Be(6);
            ShouldExposeCamelCaseProperties(root[0], "id", "name");
        }

        /// <summary>
        /// <c>GET api/products/types</c> returns <c>200 OK</c> with a JSON array of the 4 seeded types, each
        /// exposing the camelCase <c>id</c> and <c>name</c> of <c>ProductType</c>.
        /// </summary>
        [Fact]
        public async Task GetProductTypes_Seeded_Returns200With4Types()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/products/types");

            // Assert — status + array length + element shape.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.ValueKind.Should().Be(JsonValueKind.Array);
            root.GetArrayLength().Should().Be(4);
            ShouldExposeCamelCaseProperties(root[0], "id", "name");
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 2 — Basket endpoints (anonymous client)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET api/basket?id={id}</c> for an absent key returns <c>200 OK</c> with a fresh, empty
        /// <c>CustomerBasket</c> echoing the requested id: <c>id</c> equals the requested id, <c>items</c> is
        /// an empty array, and <c>shippingPrice</c> is 0. (The controller returns <c>new CustomerBasket(id)</c>
        /// when Redis has no entry.) A unique id keeps the test independent of any sibling state.
        /// </summary>
        [Fact]
        public async Task GetBasket_NonexistentId_Returns200EmptyBasket()
        {
            // Arrange — a guaranteed-absent, uniquely-keyed basket id.
            var client = _fixture.CreateClient();
            var basketId = "contract-missing-" + Guid.NewGuid();

            // Act
            var response = await client.GetAsync($"api/basket?id={Uri.EscapeDataString(basketId)}");

            // Assert — status + empty-basket contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "id", "items", "shippingPrice");
            root.GetProperty("id").GetString().Should().Be(basketId);
            root.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
            root.GetProperty("items").GetArrayLength().Should().Be(0);
            root.GetProperty("shippingPrice").GetDecimal().Should().Be(0);
        }

        /// <summary>
        /// <c>POST api/basket</c> with a valid <c>CustomerBasketDto</c> returns <c>200 OK</c> echoing the
        /// persisted basket: <c>id</c> equals the sent id and the body exposes <c>items</c>. An empty
        /// <c>items</c> list is sent — <c>CustomerBasketDto.Id</c> is the only <c>[Required]</c> member — which
        /// keeps the payload valid without needing to satisfy <c>BasketItemDto</c>'s per-item
        /// <c>[Required]</c>/<c>[Range]</c> rules. A unique id keeps the shared Redis free of cross-test collisions.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_ValidDto_Returns200EchoedBasket()
        {
            // Arrange — a minimal, valid CustomerBasketDto with a unique id and no items.
            var client = _fixture.CreateClient();
            var basketId = "contract-basket-" + Guid.NewGuid();

            // Act
            var response = await client.PostAsJsonAsync("api/basket", new { id = basketId, items = new object[0] });

            // Assert — status + echoed-basket contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "id", "items");
            root.GetProperty("id").GetString().Should().Be(basketId);
            root.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        }

        /// <summary>
        /// <c>DELETE api/basket?id={id}</c> returns <c>200 OK</c>. The basket is first created via
        /// <c>POST api/basket</c> (unique id) so a real key is deleted; the delete action returns
        /// <c>Task</c> (void), producing an empty <c>200</c> body, so only the transport status is asserted.
        /// </summary>
        [Fact]
        public async Task DeleteBasket_ExistingId_Returns200()
        {
            // Arrange — create a real basket to delete, uniquely keyed.
            var client = _fixture.CreateClient();
            var basketId = "contract-delete-" + Guid.NewGuid();
            var created = await client.PostAsJsonAsync("api/basket", new { id = basketId, items = new object[0] });
            created.StatusCode.Should().Be(HttpStatusCode.OK);

            // Act
            var response = await client.DeleteAsync($"api/basket?id={Uri.EscapeDataString(basketId)}");

            // Assert — void action => empty 200 body; assert the transport status only.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 3 — Account endpoints
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET api/account</c> without credentials returns <c>401 Unauthorized</c> — the endpoint is
        /// <c>[Authorize]</c> and must reject anonymous callers.
        /// </summary>
        [Fact]
        public async Task GetCurrentUser_Unauthenticated_Returns401()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/account");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// <c>GET api/account/emailexists?email=bob@test.com</c> for the seeded user returns <c>200 OK</c>
        /// with the JSON boolean literal <c>true</c>.
        /// </summary>
        [Fact]
        public async Task CheckEmailExists_SeededUser_Returns200True()
        {
            // Arrange
            var client = _fixture.CreateClient();
            var email = CustomWebApplicationFactory.DefaultTestUserEmail; // bob@test.com

            // Act
            var response = await client.GetAsync($"api/account/emailexists?email={Uri.EscapeDataString(email)}");

            // Assert — status + boolean literal contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.ValueKind.Should().Be(JsonValueKind.True);
            root.GetBoolean().Should().BeTrue();
        }

        /// <summary>
        /// <c>GET api/account/emailexists?email=...</c> for an unknown, uniquely-generated e-mail returns
        /// <c>200 OK</c> with the JSON boolean literal <c>false</c>.
        /// </summary>
        [Fact]
        public async Task CheckEmailExists_UnknownEmail_Returns200False()
        {
            // Arrange — a guaranteed-unknown address.
            var client = _fixture.CreateClient();
            var email = "nobody-" + Guid.NewGuid().ToString("N") + "@test.com";

            // Act
            var response = await client.GetAsync($"api/account/emailexists?email={Uri.EscapeDataString(email)}");

            // Assert — status + boolean literal contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.ValueKind.Should().Be(JsonValueKind.False);
            root.GetBoolean().Should().BeFalse();
        }

        /// <summary>
        /// <c>POST api/account/login</c> with the seeded user's valid credentials returns <c>200 OK</c> with
        /// a <c>UserDto</c>: camelCase <c>email</c> (equal to the login e-mail), <c>displayName</c> present,
        /// and a non-empty <c>token</c> (the issued JWT).
        /// </summary>
        [Fact]
        public async Task Login_ValidCredentials_Returns200UserDto()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.PostAsJsonAsync(
                "api/account/login",
                new
                {
                    email = CustomWebApplicationFactory.DefaultTestUserEmail,
                    password = CustomWebApplicationFactory.DefaultTestUserPassword
                });

            // Assert — status + UserDto contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "email", "displayName", "token");
            root.GetProperty("email").GetString().Should().Be(CustomWebApplicationFactory.DefaultTestUserEmail);
            root.GetProperty("token").GetString().Should().NotBeNullOrWhiteSpace();
        }

        /// <summary>
        /// <c>POST api/account/login</c> with a wrong password returns <c>401 Unauthorized</c> and the
        /// structured <c>ApiResponse</c> body carrying camelCase <c>statusCode == 401</c>.
        /// </summary>
        [Fact]
        public async Task Login_WrongPassword_Returns401()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.PostAsJsonAsync(
                "api/account/login",
                new { email = CustomWebApplicationFactory.DefaultTestUserEmail, password = "wrongpass" });

            // Assert — status + ApiResponse contract.
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode");
            root.GetProperty("statusCode").GetInt32().Should().Be(401);
        }

        /// <summary>
        /// <c>POST api/account/register</c> with an otherwise-VALID DTO whose e-mail already exists returns
        /// <c>400 BadRequest</c> with the custom <c>ApiValidationErrorResponose</c>: a camelCase <c>errors</c>
        /// array containing "Email address already in use". The DTO is deliberately valid (a well-formed
        /// e-mail and the policy-compliant password <c>Pa$$w0rd</c>) so the <c>[ApiController]</c> automatic
        /// model-validation passes and the controller's own email-in-use branch is reached — rather than a
        /// framework <c>ValidationProblemDetails</c> 400.
        /// </summary>
        [Fact]
        public async Task Register_ExistingEmail_Returns400ValidationErrors()
        {
            // Arrange — a fully valid DTO whose e-mail collides with the seeded user.
            var client = _fixture.CreateClient();

            // Act
            var response = await client.PostAsJsonAsync(
                "api/account/register",
                new
                {
                    displayName = "Bob",
                    email = CustomWebApplicationFactory.DefaultTestUserEmail,
                    password = CustomWebApplicationFactory.DefaultTestUserPassword
                });

            // Assert — status + validation-error contract.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "errors");
            var errors = root.GetProperty("errors");
            errors.ValueKind.Should().Be(JsonValueKind.Array);
            errors.EnumerateArray().Select(e => e.GetString())
                .Should().Contain("Email address already in use");
        }

        /// <summary>
        /// <c>POST api/account/register</c> with a valid DTO and a unique e-mail returns <c>200 OK</c> with a
        /// <c>UserDto</c> whose <c>email</c> equals the registered address and whose <c>token</c> is a
        /// non-empty JWT. A unique e-mail keeps the shared identity database free of cross-run pollution.
        /// </summary>
        [Fact]
        public async Task Register_NewUniqueUser_Returns200UserDto()
        {
            // Arrange — a unique, policy-compliant registration (password Pa$$w0rd satisfies RegisterDto's regex).
            var client = _fixture.CreateClient();
            var email = "contract-" + Guid.NewGuid().ToString("N") + "@test.com";

            // Act
            var response = await client.PostAsJsonAsync(
                "api/account/register",
                new { displayName = "Contract", email, password = CustomWebApplicationFactory.DefaultTestUserPassword });

            // Assert — status + UserDto contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "email", "token");
            root.GetProperty("email").GetString().Should().Be(email);
            root.GetProperty("token").GetString().Should().NotBeNullOrWhiteSpace();
        }

        /// <summary>
        /// <c>GET api/account</c> with a valid bearer token returns <c>200 OK</c> with the current user's
        /// <c>UserDto</c>: <c>email</c> equal to the seeded user and a non-empty <c>token</c>.
        /// </summary>
        [Fact]
        public async Task GetCurrentUser_Authenticated_Returns200UserDto()
        {
            // Arrange — an authenticated client for the seeded user.
            var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            var response = await client.GetAsync("api/account");

            // Assert — status + UserDto contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "email", "token");
            root.GetProperty("email").GetString().Should().Be(CustomWebApplicationFactory.DefaultTestUserEmail);
            root.GetProperty("token").GetString().Should().NotBeNullOrWhiteSpace();
        }

        /// <summary>
        /// <c>GET api/account/address</c> with a valid bearer token returns <c>200 OK</c> with the seeded
        /// user's <c>AddressDto</c>, exposing camelCase <c>id</c>, <c>firstName</c>, <c>lastName</c>,
        /// <c>street</c>, <c>city</c>, <c>state</c> and <c>zipCode</c>.
        /// </summary>
        [Fact]
        public async Task GetUserAddress_Authenticated_Returns200AddressDto()
        {
            // Arrange — an authenticated client for the seeded user (who has a pre-seeded address).
            var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            var response = await client.GetAsync("api/account/address");

            // Assert — status + AddressDto contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(
                root, "id", "firstName", "lastName", "street", "city", "state", "zipCode");
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 4 — Orders endpoints ([Authorize] at the class level => every action requires auth)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET api/orders</c> without credentials returns <c>401 Unauthorized</c> — the whole
        /// <c>OrdersController</c> is <c>[Authorize]</c>.
        /// </summary>
        [Fact]
        public async Task GetOrders_Unauthenticated_Returns401()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/orders");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// <c>GET api/orders</c> with a valid bearer token returns <c>200 OK</c> whose body is a JSON array
        /// (possibly empty — the seeded user need not have any orders). Asserting the array shape keeps the
        /// contract stable without coupling to non-deterministic order contents.
        /// </summary>
        [Fact]
        public async Task GetOrders_Authenticated_Returns200List()
        {
            // Arrange
            var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            var response = await client.GetAsync("api/orders");

            // Assert — status + array shape.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.ValueKind.Should().Be(JsonValueKind.Array);
        }

        /// <summary>
        /// <c>GET api/orders/deliveryMethods</c> with a valid bearer token returns <c>200 OK</c> with the 4
        /// seeded delivery methods, each exposing camelCase <c>id</c>, <c>shortName</c>, <c>deliveryTime</c>,
        /// <c>description</c> and <c>price</c>. (The literal <c>deliveryMethods</c> route segment takes
        /// precedence over the <c>{id}</c> parameter route.)
        /// </summary>
        [Fact]
        public async Task GetDeliveryMethods_Authenticated_Returns200With4Methods()
        {
            // Arrange
            var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            var response = await client.GetAsync("api/orders/deliveryMethods");

            // Assert — status + array length + element shape.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.ValueKind.Should().Be(JsonValueKind.Array);
            root.GetArrayLength().Should().Be(4);
            ShouldExposeCamelCaseProperties(
                root[0], "id", "shortName", "deliveryTime", "description", "price");
        }

        /// <summary>
        /// <c>GET api/orders/9999</c> (an id owned by no order for the seeded user) with a valid bearer token
        /// returns <c>404 NotFound</c> with the structured <c>ApiResponse</c> body (<c>statusCode == 404</c>).
        /// </summary>
        [Fact]
        public async Task GetOrderById_NonexistentId_Returns404()
        {
            // Arrange
            var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            var response = await client.GetAsync("api/orders/9999");

            // Assert — status + ApiResponse contract.
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode");
            root.GetProperty("statusCode").GetInt32().Should().Be(404);
        }

        /// <summary>
        /// <c>POST api/orders</c> with a valid-shaped <c>OrderDto</c> whose <c>basketId</c> references a
        /// non-existent basket returns a structured <c>500 InternalServerError</c> (<c>statusCode == 500</c>
        /// with a non-empty <c>message</c>). All <c>AddressDto</c> members are <c>[Required]</c>, so a
        /// complete address is supplied to pass <c>[ApiController]</c> model validation and reach the action.
        /// </summary>
        /// <remarks>
        /// OBSERVED-BEHAVIOR ALIGNMENT (verified at runtime against the real pipeline): for a missing basket,
        /// <c>IBasketRepository.GetBasketAsync</c> returns <c>null</c> and
        /// <c>OrderService.CreateOrderAsync</c> immediately dereferences it (<c>foreach (var item in
        /// basket.Items)</c>), throwing a <see cref="NullReferenceException"/> that the global
        /// <c>ExceptionMiddleware</c> surfaces as a controlled, structured <c>ApiException</c> 500. The
        /// controller's <c>400 "Problem creating order"</c> branch is only taken when
        /// <c>CreateOrderAsync</c> returns <c>null</c> (i.e. <c>UnitOfWork.Complete() &lt;= 0</c>), which is
        /// not reachable via a straightforward HTTP call with a non-existent basket and is covered by the
        /// <c>OrderService</c>/<c>OrdersController</c> unit tests instead. This assertion therefore locks the
        /// genuine HTTP contract (a structured 500) for the missing-basket input without weakening the
        /// body-shape contract (statusCode + message), and no production code is modified.
        /// </remarks>
        [Fact]
        public async Task CreateOrder_NonexistentBasket_Returns500()
        {
            // Arrange — a valid OrderDto whose basket does not exist (uniquely keyed) and a complete address.
            var client = await _fixture.CreateAuthenticatedClientAsync();
            var payload = new
            {
                basketId = "no-basket-" + Guid.NewGuid(),
                deliveryMethodId = 1,
                shipToAddress = new
                {
                    id = 1,
                    firstName = "Bob",
                    lastName = "Bobbity",
                    street = "10 The Street",
                    city = "NY",
                    state = "NY",
                    zipCode = "90210"
                }
            };

            // Act
            var response = await client.PostAsJsonAsync("api/orders", payload);

            // Assert — status + structured ApiException contract (statusCode + non-empty message).
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message");
            root.GetProperty("statusCode").GetInt32().Should().Be(500);
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 5 — Payments endpoint (backed by the offline StripePaymentServiceStub; no live Stripe)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>POST api/payments/{basketId}</c> without credentials returns <c>401 Unauthorized</c> — the
        /// action is <c>[Authorize]</c>, so an anonymous caller is rejected before any payment logic runs.
        /// </summary>
        [Fact]
        public async Task CreatePaymentIntent_Unauthenticated_Returns401()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act — no body is needed (basketId comes from the route); [Authorize] short-circuits first.
            var response = await client.PostAsync("api/payments/anybasket", null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// <c>POST api/payments/{basketId}</c> with a valid bearer token returns <c>200 OK</c> with a
        /// <c>CustomerBasket</c> carrying the offline stub's deterministic payment-intent fields:
        /// camelCase <c>id</c> equal to the requested basket id, <c>paymentIntentId == "pi_test_stub"</c> and
        /// <c>clientSecret == "pi_test_stub_secret"</c>.
        /// </summary>
        /// <remarks>
        /// The controller's <c>400 "Problem with your basket"</c> branch is NOT reachable here: the harness's
        /// <see cref="StripePaymentServiceStub"/> always returns a non-null basket. That null-basket branch is
        /// covered by the <c>PaymentsController</c> unit tests instead, per the AAP scope split.
        /// </remarks>
        [Fact]
        public async Task CreatePaymentIntent_Authenticated_Returns200BasketWithStubIntent()
        {
            // Arrange — an authenticated client and a uniquely-keyed basket id.
            var client = await _fixture.CreateAuthenticatedClientAsync();
            var basketId = "contract-pi-" + Guid.NewGuid();

            // Act — no body needed; the stub echoes the route basketId with fixed payment-intent fields.
            var response = await client.PostAsync($"api/payments/{basketId}", null);

            // Assert — status + stubbed payment-intent contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "id", "paymentIntentId", "clientSecret");
            root.GetProperty("id").GetString().Should().Be(basketId);
            root.GetProperty("paymentIntentId").GetString().Should().Be("pi_test_stub");
            root.GetProperty("clientSecret").GetString().Should().Be("pi_test_stub_secret");
        }

        /// <summary>
        /// Optional, documented smoke test of the webhook endpoint's ERROR contract: <c>POST
        /// api/payments/webhook</c> with NO <c>Stripe-Signature</c> header causes the Stripe SDK's
        /// <c>EventUtility.ConstructEvent</c> to throw, which the global <c>ExceptionMiddleware</c> surfaces
        /// as a structured <c>500</c> (<c>statusCode == 500</c> + non-empty <c>message</c>). This is entirely
        /// offline — no Stripe network call and no signing. It deliberately does NOT cover the offline-signed
        /// valid/invalid webhook success paths, which live in the sibling <c>Payments/StripeWebhookTests.cs</c>.
        /// </summary>
        [Fact]
        public async Task StripeWebhook_UnsignedRequest_Returns500()
        {
            // Arrange — an unsigned webhook POST (no Stripe-Signature header).
            var client = _fixture.CreateClient();
            using var content = new StringContent("{}");

            // Act — ConstructEvent throws on the missing/invalid signature => middleware returns a 500.
            var response = await client.PostAsync("api/payments/webhook", content);

            // Assert — status + structured ApiException contract.
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message");
            root.GetProperty("statusCode").GetInt32().Should().Be(500);
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 6 — Errors route (errors/{code}; NOTE: NO api/ prefix)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET errors/{code}</c> returns an <c>ApiResponse</c> body whose camelCase <c>statusCode</c>
        /// equals the requested code and whose <c>message</c> is the non-empty default for that code.
        /// </summary>
        /// <remarks>
        /// Transport-status caveat: <c>ErrorController.Error</c> returns <c>new ObjectResult(new
        /// ApiResponse(code))</c> WITHOUT an explicit status, so for this DIRECT route the HTTP transport
        /// status is <c>200 OK</c> (not <paramref name="code"/>). The contract that matters is the BODY's
        /// <c>statusCode</c> field, so this test asserts the body and deliberately does NOT assert
        /// <c>response.StatusCode == code</c>. (This route is normally reached indirectly via
        /// <c>UseStatusCodePagesWithReExecute("/errors/{0}")</c>, which preserves the original status.)
        /// </remarks>
        /// <param name="code">The HTTP status code embedded in the route.</param>
        [Theory]
        [InlineData(400)]
        [InlineData(401)]
        [InlineData(404)]
        [InlineData(500)]
        public async Task Error_StatusCode_ReturnsApiResponseBody(int code)
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync($"errors/{code}");

            // Assert — the BODY carries the code (transport status is 200 for the direct route; not asserted).
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message");
            root.GetProperty("statusCode").GetInt32().Should().Be(code);
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 7 — Buggy endpoints (exercise the error/status pipeline)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>GET api/buggy/testauth</c> without credentials returns <c>401 Unauthorized</c> — the action is
        /// <c>[Authorize]</c>.
        /// </summary>
        [Fact]
        public async Task Buggy_TestAuth_Unauthenticated_Returns401()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/buggy/testauth");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// <c>GET api/buggy/testauth</c> with a valid bearer token returns <c>200 OK</c> whose body contains
        /// the secret text. (The action returns a raw <c>string</c>, which serializes as a JSON string
        /// literal, so the raw response content contains "secret stuff".)
        /// </summary>
        [Fact]
        public async Task Buggy_TestAuth_Authenticated_Returns200SecretText()
        {
            // Arrange
            var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            var response = await client.GetAsync("api/buggy/testauth");

            // Assert — status + payload substring.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("secret stuff");
        }

        /// <summary>
        /// <c>GET api/buggy/notfound</c> returns <c>404 NotFound</c> with <c>statusCode == 404</c>. The
        /// action looks up product id 42, which is absent among the 18 seeded products (ids 1–18), so it
        /// returns <c>NotFound(new ApiResponse(404))</c>.
        /// </summary>
        [Fact]
        public async Task Buggy_NotFound_Returns404()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/buggy/notfound");

            // Assert — status + ApiResponse contract.
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode");
            root.GetProperty("statusCode").GetInt32().Should().Be(404);
        }

        /// <summary>
        /// <c>GET api/buggy/servererror</c> returns <c>500 InternalServerError</c> with a structured
        /// <c>ApiException</c> body (<c>statusCode == 500</c> and a non-empty <c>message</c>). The action
        /// dereferences a null lookup result, and the resulting exception is surfaced by
        /// <c>ExceptionMiddleware</c>. (In the Development environment the body also includes
        /// <c>details</c>/stack trace, but the essential contract is <c>statusCode</c> + <c>message</c>.)
        /// </summary>
        [Fact]
        public async Task Buggy_ServerError_Returns500ApiException()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/buggy/servererror");

            // Assert — status + structured ApiException contract.
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message");
            root.GetProperty("statusCode").GetInt32().Should().Be(500);
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }

        /// <summary>
        /// <c>GET api/buggy/badrequest</c> returns <c>400 BadRequest</c> with <c>statusCode == 400</c>.
        /// </summary>
        [Fact]
        public async Task Buggy_BadRequest_Returns400()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/buggy/badrequest");

            // Assert — status + ApiResponse contract.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode");
            root.GetProperty("statusCode").GetInt32().Should().Be(400);
        }

        /// <summary>
        /// <c>GET api/buggy/badrequest/5</c> (the id-bearing overload) returns <c>200 OK</c> with an empty
        /// body; only the transport status is asserted.
        /// </summary>
        [Fact]
        public async Task Buggy_BadRequestWithId_Returns200()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/buggy/badrequest/5");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ---------------------------------------------------------------------------------------------
        // Parametrized sweeps (complement the specific-shape tests above)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Every anonymous, publicly-readable <c>GET</c> endpoint returns <c>200 OK</c> with a JSON body
        /// (object or array). This is a breadth sweep that complements — but does not replace — the
        /// specific-shape assertions for each endpoint above.
        /// </summary>
        /// <param name="path">The relative endpoint path.</param>
        [Theory]
        [InlineData("api/products")]
        [InlineData("api/products/brands")]
        [InlineData("api/products/types")]
        public async Task AnonymousGetEndpoint_PublicRoute_Returns200WithJsonBody(string path)
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync(path);

            // Assert — status + a structured JSON body (object or array).
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.ValueKind.Should().BeOneOf(JsonValueKind.Object, JsonValueKind.Array);
        }

        /// <summary>
        /// Every <c>[Authorize]</c>-protected <c>GET</c> endpoint rejects an anonymous caller with
        /// <c>401 Unauthorized</c>. This guards the authorization gate on the enumerated protected routes.
        /// </summary>
        /// <param name="path">The relative endpoint path.</param>
        [Theory]
        [InlineData("api/account")]
        [InlineData("api/account/address")]
        [InlineData("api/orders")]
        [InlineData("api/orders/deliveryMethods")]
        [InlineData("api/buggy/testauth")]
        public async Task AuthorizedGetEndpoint_Anonymous_Returns401(string path)
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync(path);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
