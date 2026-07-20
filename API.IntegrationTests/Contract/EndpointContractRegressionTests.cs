using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;   // net5.0 shared-framework extension: PostAsJsonAsync / ReadFromJsonAsync
using System.Text.Json;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using Core.Interfaces;                          // IPaymentService — resolve the singleton stub (MJ-10)
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection; // GetRequiredService — resolve the singleton stub (MJ-10)
using Xunit;

namespace API.IntegrationTests.Contract
{
    /// <summary>
    /// HTTP <b>contract-regression</b> integration tests. This class is the contract-stability safety net
    /// for the whole API surface (AAP §0.1.1 Part B, §0.4.1, §0.5.1/§0.5.2): it drives the <b>real</b>
    /// ASP.NET Core application in-process — through the shared <see cref="CustomWebApplicationFactory"/>
    /// over genuine <see cref="HttpClient"/>s — against <b>real</b> PostgreSQL and Redis provisioned by
    /// Testcontainers (via this class's dedicated <see cref="ContainerFixture"/>) with the documented seed data, and
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
    /// <b>Per-class isolated containers (CR-01).</b> The class consumes <see cref="ContainerFixture"/> as an
    /// <c>IClassFixture&lt;ContainerFixture&gt;</c>, so xUnit provisions a dedicated PostgreSQL + Redis pair
    /// (and in-process host) for THIS class alone — started once before its first test and disposed once
    /// after its last (AAP §0.10.1; binding Rule 1). This class is never destructive: it only reads/echoes,
    /// never stops a container. State it creates is still uniquely keyed with a <see cref="Guid"/> (basket
    /// ids, registration e-mails) so tests within the class cannot collide, and nothing can leak to any
    /// other class's isolated containers.
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
    public class EndpointContractRegressionTests : IClassFixture<ContainerFixture>
    {
        /// <summary>This class's dedicated, already-started PostgreSQL + Redis + in-process host harness (injected by xUnit).</summary>
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// Receives this class's dedicated <see cref="ContainerFixture"/>. Because the class implements
        /// <c>IClassFixture&lt;ContainerFixture&gt;</c>, xUnit constructs exactly one fixture for THIS class
        /// (its <c>IAsyncLifetime</c> runs once before the first test and once after the last) and injects
        /// it here.
        /// </summary>
        /// <param name="fixture">This class's isolated container/host fixture.</param>
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

        /// <summary>
        /// Seeds a real single-item <c>CustomerBasket</c> in Redis so that
        /// <c>OrderService.CreateOrderAsync</c> can build a genuine order server-side (MJ-12). A genuinely
        /// seeded product is discovered via <c>GET api/products</c> (ids are DB-assigned, so never
        /// hardcoded) and echoed into a <c>BasketItemDto</c> that satisfies every <c>[Required]</c>/
        /// <c>[Range]</c> rule; the basket is persisted with <c>POST api/basket</c>. Returns the
        /// uniquely-keyed basket id so the caller can delete the Redis key in a <c>finally</c>
        /// (MJ-04 deterministic cleanup). All intermediate responses are disposed (MD-01).
        /// </summary>
        /// <param name="client">The HTTP client used to seed the basket.</param>
        /// <returns>The unique basket id of the seeded single-item basket.</returns>
        private async Task<string> SeedRealSingleItemBasketAsync(HttpClient client)
        {
            using var productsResponse = await client.GetAsync("api/products");
            productsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var product = (await ReadRootAsync(productsResponse)).GetProperty("data")[0];

            var basketId = "contract-order-" + Guid.NewGuid();
            var payload = new
            {
                id = basketId,
                items = new[]
                {
                    new
                    {
                        // BasketItemDto: every member is [Required]; id is the product id the server
                        // re-prices authoritatively in CreateOrderAsync (client price is never trusted).
                        id = product.GetProperty("id").GetInt32(),
                        productName = product.GetProperty("name").GetString(),
                        price = product.GetProperty("price").GetDecimal(),
                        quantity = 1,
                        pictureUrl = product.GetProperty("pictureUrl").GetString(),
                        brand = product.GetProperty("productBrand").GetString(),
                        type = product.GetProperty("productType").GetString()
                    }
                },
                shippingPrice = 0m
            };

            using var basketResponse = await client.PostAsJsonAsync("api/basket", payload);
            basketResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            return basketId;
        }

        /// <summary>
        /// Posts a valid <c>OrderDto</c> (delivery method 1 and a complete, <c>[Required]</c>-satisfying
        /// address) for the already-seeded <paramref name="basketId"/>, asserts <c>200 OK</c>, and returns
        /// the created order's <c>id</c>. Used by the order-detail and cross-buyer-isolation tests that need
        /// a real, owned order id. The response is disposed (MD-01).
        /// </summary>
        /// <param name="client">An authenticated HTTP client (the order is owned by that client's user).</param>
        /// <param name="basketId">The id of a basket already seeded via <see cref="SeedRealSingleItemBasketAsync"/>.</param>
        /// <returns>The DB-assigned id of the newly created order.</returns>
        private async Task<int> CreateOrderReturningIdAsync(HttpClient client, string basketId)
        {
            using var response = await client.PostAsJsonAsync("api/orders", new
            {
                basketId,
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
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            return root.GetProperty("id").GetInt32();
        }

        /// <summary>
        /// Best-effort deletion of a Redis basket key created by a test, invoked from a <c>finally</c> block
        /// so no basket outlives the test that created it (MJ-04). The response is disposed (MD-01).
        /// </summary>
        /// <param name="client">The HTTP client used to delete the basket.</param>
        /// <param name="basketId">The basket id to delete.</param>
        private static async Task TryDeleteBasketAsync(HttpClient client, string basketId)
        {
            using var response = await client.DeleteAsync($"api/basket?id={Uri.EscapeDataString(basketId)}");
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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/products");

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
            using var client = _fixture.CreateClient();
            using var listResponse = await client.GetAsync("api/products"); // MD-01: dispose the discovery response too.
            var listRoot = await ReadRootAsync(listResponse);
            var existingId = listRoot.GetProperty("data")[0].GetProperty("id").GetInt32();

            // Act
            using var response = await client.GetAsync($"api/products/{existingId}");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/products/9999");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/products/brands");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/products/types");

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
            using var client = _fixture.CreateClient();
            var basketId = "contract-missing-" + Guid.NewGuid();

            // Act
            using var response = await client.GetAsync($"api/basket?id={Uri.EscapeDataString(basketId)}");

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
            using var client = _fixture.CreateClient();
            var basketId = "contract-basket-" + Guid.NewGuid();

            // Act
            using var response = await client.PostAsJsonAsync("api/basket", new { id = basketId, items = new object[0] });

            // Assert — status + echoed-basket contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "id", "items");
            root.GetProperty("id").GetString().Should().Be(basketId);
            root.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        }

        /// <summary>
        /// <c>POST api/basket</c> with a fully-populated <c>CustomerBasketDto</c> carrying one genuinely
        /// seeded product, then <c>GET api/basket</c>, returns <c>200 OK</c> and — critically for MJ-12 —
        /// locks the complete <b>BasketItem</b> wire contract: each item exposes camelCase <c>id</c>,
        /// <c>productName</c>, <c>price</c>, <c>quantity</c>, <c>pictureUrl</c>, <c>brand</c> and <c>type</c>,
        /// alongside the basket-level <c>id</c>, <c>items</c> and <c>shippingPrice</c>. This complements the
        /// minimal empty-items echo above (which only proves the envelope) by asserting the full per-item
        /// shape after a real Redis round-trip. The unique basket key is deleted in <c>finally</c> (MJ-04).
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WithSeededItem_Returns200EchoedBasketItemContract()
        {
            // Arrange — a complete, valid basket built from a genuinely-seeded product.
            using var client = _fixture.CreateClient();
            string basketId = null;
            try
            {
                basketId = await SeedRealSingleItemBasketAsync(client);

                // Act — read the persisted basket back to assert its serialized contract after a real round-trip.
                using var response = await client.GetAsync($"api/basket?id={Uri.EscapeDataString(basketId)}");

                // Assert — status + basket envelope + complete per-item BasketItem envelope.
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var root = await ReadRootAsync(response);
                ShouldExposeCamelCaseProperties(root, "id", "items", "shippingPrice");
                root.GetProperty("id").GetString().Should().Be(basketId);
                var items = root.GetProperty("items");
                items.ValueKind.Should().Be(JsonValueKind.Array);
                items.GetArrayLength().Should().Be(1);
                ShouldExposeCamelCaseProperties(
                    items[0], "id", "productName", "price", "quantity", "pictureUrl", "brand", "type");
                items[0].GetProperty("quantity").GetInt32().Should().Be(1);
            }
            finally
            {
                // MJ-04: no basket may outlive the test that created it.
                if (basketId != null) await TryDeleteBasketAsync(client, basketId);
            }
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
            using var client = _fixture.CreateClient();
            var basketId = "contract-delete-" + Guid.NewGuid();
            using var created = await client.PostAsJsonAsync("api/basket", new { id = basketId, items = new object[0] }); // MD-01: dispose intermediate response.
            created.StatusCode.Should().Be(HttpStatusCode.OK);

            // Act
            using var response = await client.DeleteAsync($"api/basket?id={Uri.EscapeDataString(basketId)}");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/account");

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
            using var client = _fixture.CreateClient();
            var email = CustomWebApplicationFactory.DefaultTestUserEmail; // bob@test.com

            // Act
            using var response = await client.GetAsync($"api/account/emailexists?email={Uri.EscapeDataString(email)}");

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
            using var client = _fixture.CreateClient();
            var email = "nobody-" + Guid.NewGuid().ToString("N") + "@test.com";

            // Act
            using var response = await client.GetAsync($"api/account/emailexists?email={Uri.EscapeDataString(email)}");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.PostAsJsonAsync(
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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.PostAsJsonAsync(
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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.PostAsJsonAsync(
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
            using var client = _fixture.CreateClient();
            var email = "contract-" + Guid.NewGuid().ToString("N") + "@test.com";

            // Act
            using var response = await client.PostAsJsonAsync(
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
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            using var response = await client.GetAsync("api/account");

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
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            using var response = await client.GetAsync("api/account/address");

            // Assert — status + AddressDto contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(
                root, "id", "firstName", "lastName", "street", "city", "state", "zipCode");
        }

        /// <summary>
        /// <c>PUT api/account/address</c> with a valid bearer token and a complete <c>AddressDto</c> returns
        /// <c>200 OK</c> echoing the updated address with the full camelCase contract (<c>id</c>,
        /// <c>firstName</c>, <c>lastName</c>, <c>street</c>, <c>city</c>, <c>state</c>, <c>zipCode</c>) and
        /// the new field values (MJ-11 success path). The seeded user's ORIGINAL address is captured first
        /// and RESTORED in a <c>finally</c> (MJ-04 state restoration) so this mutation cannot leak into any
        /// sibling test (e.g. the <c>GetUserAddress</c> contract test). All responses are disposed (MD-01).
        /// </summary>
        [Fact]
        public async Task UpdateUserAddress_AuthenticatedWithCompleteAddress_Returns200UpdatedAddressDto()
        {
            // Arrange — authenticated seeded user; capture the current address so it can be restored.
            using var client = await _fixture.CreateAuthenticatedClientAsync();
            JsonElement original;
            using (var getResponse = await client.GetAsync("api/account/address"))
            {
                getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                original = await ReadRootAsync(getResponse);
            }

            try
            {
                var updated = new
                {
                    id = original.GetProperty("id").GetInt32(),
                    firstName = "Contract",
                    lastName = "Updated",
                    street = "1 Regression Way",
                    city = "Testville",
                    state = "TS",
                    zipCode = "01010"
                };

                // Act
                using var response = await client.PutAsJsonAsync("api/account/address", updated);

                // Assert — status + complete AddressDto contract + echoed new values.
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var root = await ReadRootAsync(response);
                ShouldExposeCamelCaseProperties(
                    root, "id", "firstName", "lastName", "street", "city", "state", "zipCode");
                root.GetProperty("firstName").GetString().Should().Be("Contract");
                root.GetProperty("lastName").GetString().Should().Be("Updated");
                root.GetProperty("street").GetString().Should().Be("1 Regression Way");
                root.GetProperty("city").GetString().Should().Be("Testville");
                root.GetProperty("state").GetString().Should().Be("TS");
                root.GetProperty("zipCode").GetString().Should().Be("01010");
            }
            finally
            {
                // MJ-04: restore the seeded user's original address so no state leaks to sibling tests.
                var restore = new
                {
                    id = original.GetProperty("id").GetInt32(),
                    firstName = original.GetProperty("firstName").GetString(),
                    lastName = original.GetProperty("lastName").GetString(),
                    street = original.GetProperty("street").GetString(),
                    city = original.GetProperty("city").GetString(),
                    state = original.GetProperty("state").GetString(),
                    zipCode = original.GetProperty("zipCode").GetString()
                };
                using var restoreResponse = await client.PutAsJsonAsync("api/account/address", restore);
                restoreResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            }
        }

        /// <summary>
        /// <c>PUT api/account/address</c> with a valid bearer token but a body missing a <c>[Required]</c>
        /// string member (<c>firstName</c> omitted) is rejected by the custom
        /// <c>InvalidModelStateResponseFactory</c> with <c>400 BadRequest</c> and the
        /// <c>ApiValidationErrorResponose</c> envelope: a non-empty camelCase <c>errors</c> string array plus
        /// the inherited <c>statusCode</c>/<c>message</c> (MJ-11 validation path / MJ-12 model-validation
        /// envelope). The action body never runs, so no address is mutated and no cleanup is required.
        /// </summary>
        [Fact]
        public async Task UpdateUserAddress_MissingRequiredField_Returns400ValidationErrors()
        {
            // Arrange — authenticated, but omit the [Required] firstName so model validation fails first.
            using var client = await _fixture.CreateAuthenticatedClientAsync();
            var invalid = new
            {
                id = 1,
                lastName = "Updated",
                street = "1 Regression Way",
                city = "Testville",
                state = "TS",
                zipCode = "01010"
            };

            // Act
            using var response = await client.PutAsJsonAsync("api/account/address", invalid);

            // Assert — status + ApiValidationErrorResponose envelope (statusCode + message + errors array).
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message", "errors");
            root.GetProperty("statusCode").GetInt32().Should().Be(400);
            var errors = root.GetProperty("errors");
            errors.ValueKind.Should().Be(JsonValueKind.Array);
            errors.GetArrayLength().Should().BeGreaterThan(0);
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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/orders");

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
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            using var response = await client.GetAsync("api/orders");

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
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            using var response = await client.GetAsync("api/orders/deliveryMethods");

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
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            using var response = await client.GetAsync("api/orders/9999");

            // Assert — status + ApiResponse contract.
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode");
            root.GetProperty("statusCode").GetInt32().Should().Be(404);
        }

        /// <summary>
        /// <c>POST api/orders</c> with a valid-shaped <c>OrderDto</c> whose <c>basketId</c> references a
        /// non-existent basket CURRENTLY returns a structured <c>500 InternalServerError</c>
        /// (<c>statusCode == 500</c> with a non-empty <c>message</c>). All <c>AddressDto</c> members are
        /// <c>[Required]</c>, so a complete address is supplied to pass <c>[ApiController]</c> model
        /// validation and reach the action. This test locks the ACTUAL observed contract while EXPLICITLY
        /// documenting that it DEVIATES from the AAP §0.4.2 blueprint (which specifies a missing basket →
        /// <c>400</c>); see the remarks for the root cause and the out-of-scope escalation.
        /// </summary>
        /// <remarks>
        /// <b>DOCUMENTED AAP DEVIATION (dest GAP-3) — surfaced and escalated, not hidden.</b> The AAP §0.4.2
        /// integration blueprint specifies that a "basket not found" input should yield <c>null</c> from
        /// <c>OrderService.CreateOrderAsync</c> and a <c>400 BadRequest</c> ("Problem creating order") from
        /// <c>OrdersController</c>. The ACTUAL runtime behavior (verified against the real pipeline) is a
        /// structured <c>500</c> instead, for this root cause: for a missing basket
        /// <c>IBasketRepository.GetBasketAsync</c> returns <c>null</c> and <c>OrderService.CreateOrderAsync</c>
        /// dereferences it at its very first statement (<c>foreach (var item in basket.Items)</c>,
        /// <c>OrderService.cs</c> ~line 30) WITHOUT a null guard, throwing a
        /// <see cref="NullReferenceException"/> that the global <c>ExceptionMiddleware</c> catches and
        /// surfaces as a controlled, structured <c>ApiException</c> 500. The controller's
        /// <c>400 "Problem creating order"</c> branch is only taken when <c>CreateOrderAsync</c> returns
        /// <c>null</c> (i.e. <c>UnitOfWork.Complete() &lt;= 0</c>), which a missing basket never reaches
        /// because the NRE is thrown first.
        /// <para>
        /// <b>Why this test asserts 500 rather than the AAP's 400.</b> Closing the gap requires a PRODUCTION
        /// change — a null guard in <c>OrderService.CreateOrderAsync</c> that returns <c>null</c> for a
        /// missing basket so the controller can emit its 400. That is OUT OF SCOPE for this test-only
        /// engagement, whose production code, controllers and services are frozen (AAP §0.8.2: "No controller
        /// logic, middleware, service, repository, or entity behavior is altered"; the only permitted
        /// production seam is the annotated Stripe change in <c>PaymentService</c>). Per the QA guidance this
        /// divergence is therefore SURFACED here and ESCALATED in the resolution report rather than silently
        /// absorbed or masked: the assertion truthfully locks the current structured-500 contract (statusCode
        /// + non-empty message) so any future regression is caught, and the method name carries the
        /// <c>_DocumentedAapNullToBadRequestDeviation</c> suffix to make the known divergence explicit. No
        /// production code is modified by this test.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task CreateOrder_NonexistentBasket_Returns500_DocumentedAapNullToBadRequestDeviation()
        {
            // Arrange — a valid OrderDto whose basket does not exist (uniquely keyed) and a complete address.
            using var client = await _fixture.CreateAuthenticatedClientAsync();
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
            using var response = await client.PostAsJsonAsync("api/orders", payload);

            // Assert — status + structured ApiException contract (statusCode + non-empty message).
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message");
            root.GetProperty("statusCode").GetInt32().Should().Be(500);
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }

        /// <summary>
        /// <c>POST api/orders</c> with a valid <c>OrderDto</c> referencing a REAL seeded basket returns
        /// <c>200 OK</c> serialising the created <b>Order entity</b> (MJ-12 create contract). Locks the raw
        /// entity wire shape: camelCase <c>id</c>, <c>buyerEmail</c>, <c>orderDate</c>, <c>shipToAddress</c>
        /// (object), <c>deliveryMethod</c> (the full DeliveryMethod OBJECT — not the mapped string),
        /// <c>orderItems</c> (each with <c>itemOrdered</c>, <c>price</c>, <c>quantity</c>), <c>subtotal</c>,
        /// <c>status</c> and <c>paymentId</c>. It additionally asserts the entity exposes NO <c>total</c>
        /// member (<c>Order.GetTotal()</c> is a method, not a serialised property) and that <c>subtotal</c>
        /// is the SERVER-side price (server pricing authority). The basket is deleted in <c>finally</c>
        /// (MJ-04); the created order row lives only in this class's isolated PostgreSQL (CR-01) and is
        /// disposed at class teardown — <c>GetOrders_Authenticated</c> asserts only array shape, so it is
        /// unaffected.
        /// </summary>
        [Fact]
        public async Task CreateOrder_ValidBasketAndAddress_Returns200OrderEntityContract()
        {
            using var client = await _fixture.CreateAuthenticatedClientAsync();
            string basketId = null;
            try
            {
                basketId = await SeedRealSingleItemBasketAsync(client);

                // Act — create the order for the seeded basket with a complete address.
                using var response = await client.PostAsJsonAsync("api/orders", new
                {
                    basketId,
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
                });

                // Assert — status + raw Order-entity contract.
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var root = await ReadRootAsync(response);
                ShouldExposeCamelCaseProperties(
                    root, "id", "buyerEmail", "orderDate", "shipToAddress", "deliveryMethod",
                    "orderItems", "subtotal", "status", "paymentId");
                root.GetProperty("buyerEmail").GetString()
                    .Should().Be(CustomWebApplicationFactory.DefaultTestUserEmail);

                // shipToAddress is the OrderAggregate.Address object.
                ShouldExposeCamelCaseProperties(
                    root.GetProperty("shipToAddress"),
                    "firstName", "lastName", "street", "city", "state", "zipCode");

                // deliveryMethod on the RAW entity is the full DeliveryMethod object (contrast the DTO string).
                root.GetProperty("deliveryMethod").ValueKind.Should().Be(JsonValueKind.Object);
                ShouldExposeCamelCaseProperties(
                    root.GetProperty("deliveryMethod"),
                    "id", "shortName", "deliveryTime", "description", "price");

                // orderItems: server-priced from the seeded product; itemOrdered carries the product snapshot.
                var orderItems = root.GetProperty("orderItems");
                orderItems.ValueKind.Should().Be(JsonValueKind.Array);
                orderItems.GetArrayLength().Should().BeGreaterThan(0);
                ShouldExposeCamelCaseProperties(orderItems[0], "id", "itemOrdered", "price", "quantity");
                ShouldExposeCamelCaseProperties(
                    orderItems[0].GetProperty("itemOrdered"), "productItemId", "productName", "pictureUrl");

                // Server-side authority: subtotal derives from the DB product price, never the client's.
                root.GetProperty("subtotal").GetDecimal().Should().BeGreaterThan(0);

                // The raw entity exposes GetTotal() as a METHOD, so no 'total' member is serialised.
                root.TryGetProperty("total", out _).Should()
                    .BeFalse("the raw Order entity serialises no 'total' (GetTotal() is a method, not a property)");
            }
            finally
            {
                if (basketId != null) await TryDeleteBasketAsync(client, basketId);
            }
        }

        /// <summary>
        /// <c>GET api/orders/{id}</c> for an order OWNED by the caller returns <c>200 OK</c> with the
        /// <c>OrderToReturnDto</c> contract (MJ-12 detail contract) — deliberately DISTINCT from the raw
        /// create-entity shape: <c>deliveryMethod</c> is the mapped STRING (ShortName), <c>shippingPrice</c>
        /// and <c>total</c> ARE present, <c>orderItems</c> use <c>productId</c>/<c>productName</c>/
        /// <c>pictureUrl</c>/<c>price</c>/<c>quantity</c>, and <c>status</c> is the enum member name string
        /// "Pending". The basket is deleted in <c>finally</c> (MJ-04).
        /// </summary>
        [Fact]
        public async Task GetOrderById_OwnedOrder_Returns200OrderToReturnDtoContract()
        {
            using var client = await _fixture.CreateAuthenticatedClientAsync();
            string basketId = null;
            try
            {
                basketId = await SeedRealSingleItemBasketAsync(client);
                var orderId = await CreateOrderReturningIdAsync(client, basketId);

                // Act — fetch the just-created, caller-owned order back.
                using var response = await client.GetAsync($"api/orders/{orderId}");

                // Assert — status + OrderToReturnDto contract.
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var root = await ReadRootAsync(response);
                ShouldExposeCamelCaseProperties(
                    root, "id", "buyerEmail", "orderDate", "shipToAddress", "deliveryMethod",
                    "shippingPrice", "orderItems", "subtotal", "total", "status");
                root.GetProperty("id").GetInt32().Should().Be(orderId);
                root.GetProperty("buyerEmail").GetString()
                    .Should().Be(CustomWebApplicationFactory.DefaultTestUserEmail);

                // On the DTO, deliveryMethod is the mapped ShortName STRING (not the entity object).
                root.GetProperty("deliveryMethod").ValueKind.Should().Be(JsonValueKind.String);

                // OrderItemDto per-item contract (productId, not the entity's itemOrdered snapshot).
                var orderItems = root.GetProperty("orderItems");
                orderItems.ValueKind.Should().Be(JsonValueKind.Array);
                orderItems.GetArrayLength().Should().BeGreaterThan(0);
                ShouldExposeCamelCaseProperties(
                    orderItems[0], "productId", "productName", "pictureUrl", "price", "quantity");

                // AutoMapper maps the OrderStatus enum to its member NAME; a freshly created order is "Pending".
                root.GetProperty("status").GetString().Should().Be("Pending");
            }
            finally
            {
                if (basketId != null) await TryDeleteBasketAsync(client, basketId);
            }
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
            using var client = _fixture.CreateClient();

            // Act — no body is needed (basketId comes from the route); [Authorize] short-circuits first.
            using var response = await client.PostAsync("api/payments/anybasket", null);

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
        /// This test exercises the happy path where the stub returns its default non-null basket. The
        /// controller's <c>400 "Problem with your basket"</c> branch is covered by the sibling
        /// <see cref="CreatePaymentIntent_WhenStubReturnsNullBasket_Returns400ProblemWithBasket"/>, which
        /// opts the shared <see cref="StripePaymentServiceStub"/> into returning a null basket so that
        /// fail-path is driven through the real HTTP pipeline (in addition to the <c>PaymentsController</c>
        /// unit tests, per the AAP scope split).
        /// </remarks>
        [Fact]
        public async Task CreatePaymentIntent_Authenticated_Returns200BasketWithStubIntent()
        {
            // Arrange — an authenticated client and a uniquely-keyed basket id.
            using var client = await _fixture.CreateAuthenticatedClientAsync();
            var basketId = "contract-pi-" + Guid.NewGuid();

            // Act — no body needed; the stub echoes the route basketId with fixed payment-intent fields.
            using var response = await client.PostAsync($"api/payments/{basketId}", null);

            // Assert — status + stubbed payment-intent contract.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "id", "paymentIntentId", "clientSecret");
            root.GetProperty("id").GetString().Should().Be(basketId);
            root.GetProperty("paymentIntentId").GetString().Should().Be("pi_test_stub");
            root.GetProperty("clientSecret").GetString().Should().Be("pi_test_stub_secret");
        }

        /// <summary>
        /// <c>POST api/payments/{basketId}</c> with a valid bearer token but when the payment service yields
        /// a <b>null</b> basket returns <c>400 Bad Request</c> with the structured <c>ApiResponse</c> body
        /// (camelCase <c>statusCode == 400</c> and <c>message == "Problem with your basket"</c>).
        /// </summary>
        /// <remarks>
        /// This drives <c>PaymentsController.CreateOrUpdatePaymentIntent</c>'s guard
        /// <c>if (basket == null) return BadRequest(new ApiResponse(400, "Problem with your basket"))</c>
        /// through the REAL HTTP pipeline. The default offline <see cref="StripePaymentServiceStub"/> always
        /// returns a non-null basket, so this branch was previously unreachable in integration (w014 finding
        /// B). We opt the shared singleton stub into returning a null basket for the duration of this test
        /// via <see cref="StripePaymentServiceStub.SetCreateOrUpdatePaymentIntentReturnsNull(bool)"/>, then
        /// restore the default in a <c>finally</c>
        /// (<see cref="StripePaymentServiceStub.ResetCreateOrUpdatePaymentIntentBehavior"/>) so no behavior
        /// leaks to subsequent sequentially-run tests. No live Stripe call is made (AAP §0.10.1); the toggle
        /// is a pure in-memory, test-project-only switch and changes NO production code.
        /// </remarks>
        [Fact]
        public async Task CreatePaymentIntent_WhenStubReturnsNullBasket_Returns400ProblemWithBasket()
        {
            // Arrange — an authenticated client and the shared singleton payment-service stub the running
            // application resolves per request (registered as a singleton by CustomWebApplicationFactory,
            // so this is the SAME instance the controller uses).
            using var client = await _fixture.CreateAuthenticatedClientAsync();
            var stub = (StripePaymentServiceStub)_fixture.Factory.Services.GetRequiredService<IPaymentService>();
            var basketId = "contract-pi-null-" + Guid.NewGuid();

            // Opt this single test into the null-basket outcome so the controller's 400 guard is reached.
            stub.SetCreateOrUpdatePaymentIntentReturnsNull(true);

            try
            {
                // Act — the stub now yields a null basket, so the controller must short-circuit to 400.
                using var response = await client.PostAsync($"api/payments/{basketId}", null);

                // Assert — status + structured ApiResponse contract (statusCode + message).
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                var root = await ReadRootAsync(response);
                ShouldExposeCamelCaseProperties(root, "statusCode", "message");
                root.GetProperty("statusCode").GetInt32().Should().Be(400);
                root.GetProperty("message").GetString().Should().Be("Problem with your basket");
            }
            finally
            {
                // Restore the default non-null behavior so no state leaks to later tests on the shared singleton.
                stub.ResetCreateOrUpdatePaymentIntentBehavior();
            }
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
            using var client = _fixture.CreateClient();
            using var content = new StringContent("{}");

            // Act — ConstructEvent throws on the missing/invalid signature => middleware returns a 500.
            using var response = await client.PostAsync("api/payments/webhook", content);

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync($"errors/{code}");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/buggy/testauth");

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
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act
            using var response = await client.GetAsync("api/buggy/testauth");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/buggy/notfound");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/buggy/servererror");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/buggy/badrequest");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync("api/buggy/badrequest/5");

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync(path);

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
            using var client = _fixture.CreateClient();

            // Act
            using var response = await client.GetAsync(path);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// Every <c>[Authorize]</c>-protected route rejects an anonymous caller with <c>401 Unauthorized</c>
        /// across the NON-GET verbs and parameterised paths that the GET-only sweep above omits (MJ-13):
        /// <c>POST api/orders</c>, <c>GET api/orders/{id}</c>, <c>PUT api/account/address</c> and
        /// <c>POST api/payments/{basketId}</c>. The authorization middleware short-circuits before any model
        /// binding, so a bodyless request is sufficient to observe the gate. The request and response are
        /// disposed (MD-01).
        /// </summary>
        /// <param name="method">The HTTP verb to exercise.</param>
        /// <param name="path">The protected relative path.</param>
        [Theory]
        [InlineData("POST", "api/orders")]
        [InlineData("GET", "api/orders/1")]
        [InlineData("PUT", "api/account/address")]
        [InlineData("POST", "api/payments/any-basket")]
        public async Task ProtectedEndpoint_AnonymousAcrossVerbs_Returns401(string method, string path)
        {
            // Arrange — an anonymous client and a bodyless request for the given verb/path.
            using var client = _fixture.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), path);

            // Act — authorization runs before model binding, so no body is needed.
            using var response = await client.SendAsync(request);

            // Assert — the gate rejects the anonymous caller.
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// Cross-buyer authorization isolation (MJ-13): a genuine order created by the seeded buyer
        /// (<c>bob@test.com</c>) CANNOT be read by a different, freshly-registered buyer. The seeded buyer
        /// first creates a real order and confirms he can read it (so the <c>404</c> below is a true scoping
        /// result, not a missing-order false positive); a second, distinct buyer then requests the SAME
        /// order id and receives <c>404 NotFound</c> because <c>OrderService.GetOrderByIdAsync</c> filters by
        /// <c>buyerEmail</c> as well as id. The basket is deleted in <c>finally</c> (MJ-04); the created order
        /// and the second identity user live only in this class's isolated PostgreSQL/Identity databases
        /// (CR-01) and are discarded at class teardown — there is no production API to delete either, and a
        /// second user is required to prove the isolation.
        /// </summary>
        [Fact]
        public async Task GetOrderById_OrderOwnedByAnotherBuyer_Returns404()
        {
            // Arrange — the seeded buyer creates a real, owned order.
            using var bobClient = await _fixture.CreateAuthenticatedClientAsync();
            string basketId = null;
            try
            {
                basketId = await SeedRealSingleItemBasketAsync(bobClient);
                var bobOrderId = await CreateOrderReturningIdAsync(bobClient, basketId);

                // Sanity — the owner CAN read his own order, so the id is real and the 404 below is genuine isolation.
                using (var bobFetch = await bobClient.GetAsync($"api/orders/{bobOrderId}"))
                {
                    bobFetch.StatusCode.Should().Be(HttpStatusCode.OK);
                }

                // Register + authenticate a SECOND, distinct buyer (unique e-mail keeps the identity DB clean).
                var otherEmail = "contract-other-" + Guid.NewGuid().ToString("N") + "@test.com";
                using (var anon = _fixture.CreateClient())
                using (var registration = await anon.PostAsJsonAsync(
                    "api/account/register",
                    new
                    {
                        displayName = "Other",
                        email = otherEmail,
                        password = CustomWebApplicationFactory.DefaultTestUserPassword
                    }))
                {
                    registration.StatusCode.Should().Be(HttpStatusCode.OK);
                }

                using var otherClient = await _fixture.CreateAuthenticatedClientAsync(
                    otherEmail, CustomWebApplicationFactory.DefaultTestUserPassword);

                // Act — the second buyer requests the first buyer's order id.
                using var response = await otherClient.GetAsync($"api/orders/{bobOrderId}");

                // Assert — buyer-scoped lookup yields 404 (not 200 and not 403) with the ApiResponse envelope.
                response.StatusCode.Should().Be(HttpStatusCode.NotFound);
                var root = await ReadRootAsync(response);
                ShouldExposeCamelCaseProperties(root, "statusCode");
                root.GetProperty("statusCode").GetInt32().Should().Be(404);
            }
            finally
            {
                if (basketId != null) await TryDeleteBasketAsync(bobClient, basketId);
            }
        }

        // ------------------------------ FINDING J — pagination boundary contract ------------------------------
        // Documents the ACTUAL (verified-at-runtime) behavior of GET api/products at its pagination edges.
        // The paging spec computes Skip = PageSize*(PageIndex-1), Take = PageSize
        // (ProductsWithTypesAndBrandsSpecification.ApplyPaging), and SpecificationEvaluator applies
        // .Skip(Skip).Take(Take) directly to the EF Core query. Consequently a non-positive pageIndex or a
        // negative pageSize produces a NEGATIVE OFFSET/LIMIT that real PostgreSQL rejects at execution time,
        // which the global ExceptionMiddleware surfaces as a structured 500 (fail-closed). These are
        // PRE-EXISTING production characteristics (no input clamping/guarding on the lower bound); the tests
        // PIN them rather than mask them. Production code is NOT modified.

        /// <summary>
        /// FINDING J — non-positive <c>pageIndex</c> or negative <c>pageSize</c> yields a structured
        /// <c>500 InternalServerError</c>. Each case drives a negative SQL <c>OFFSET</c>/<c>LIMIT</c> that
        /// real PostgreSQL rejects, surfaced by <c>ExceptionMiddleware</c> as an <c>ApiException</c>
        /// (<c>statusCode == 500</c> + non-empty <c>message</c>). Documented, unmodified production behavior.
        /// </summary>
        [Theory]
        [InlineData("api/products?pageIndex=0")]    // Skip = 6*(0-1)  = -6  => negative OFFSET
        [InlineData("api/products?pageIndex=-1")]   // Skip = 6*(-1-1) = -12 => negative OFFSET
        [InlineData("api/products?pageSize=-5")]    // Take = -5             => negative LIMIT
        public async Task GetProducts_NonPositivePagingBound_Returns500_DocumentedFailClosed(string path)
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync(path);

            // Assert — real PostgreSQL rejects the negative OFFSET/LIMIT => structured 500 (fail-closed).
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message");
            root.GetProperty("statusCode").GetInt32().Should().Be(500);
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }

        /// <summary>
        /// FINDING J — an excessive <c>pageSize</c> is CLAMPED to <c>ProductSpecParams.MaxPageSize</c> (50):
        /// <c>GET api/products?pageSize=100000</c> returns <c>200 OK</c> with the envelope's <c>pageSize == 50</c>
        /// (the setter's documented upper-bound clamp), and the <c>data</c> array holds at most 50 items.
        /// Confirms the upper-bound guard is intact (only the lower bound is unguarded — see the 500 theory).
        /// </summary>
        [Fact]
        public async Task GetProducts_ExcessivePageSize_Returns200ClampedTo50()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act
            var response = await client.GetAsync("api/products?pageSize=100000");

            // Assert — 200 + pageSize clamped to the MaxPageSize (50).
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.GetProperty("pageSize").GetInt32().Should().Be(50, "PageSize is clamped to MaxPageSize (50)");
            root.GetProperty("data").GetArrayLength().Should().BeLessOrEqualTo(50);
        }

        /// <summary>
        /// FINDING J — a non-integer <c>pageIndex</c> fails model binding, so <c>[ApiController]</c> returns
        /// an automatic <c>400 BadRequest</c> (a <c>ValidationProblemDetails</c>) BEFORE the action runs —
        /// distinct from the negative-bound 500s, which occur DURING query execution.
        /// </summary>
        [Fact]
        public async Task GetProducts_NonIntegerPageIndex_Returns400()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act — "abc" cannot bind to int PageIndex => automatic model-validation 400.
            var response = await client.GetAsync("api/products?pageIndex=abc");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        /// <summary>
        /// FINDING J — a <c>pageIndex</c> far beyond the available data returns <c>200 OK</c> with an EMPTY
        /// <c>data</c> array while <c>count</c> still reports the true total (the seeded 18). Confirms
        /// over-paging is a valid, non-erroring case (a positive Skip past the end simply yields no rows).
        /// </summary>
        [Fact]
        public async Task GetProducts_PageIndexBeyondData_Returns200EmptyData()
        {
            // Arrange
            var client = _fixture.CreateClient();

            // Act — page 999999 of size 6 => Skip is huge (positive) => no rows, but the query is valid.
            var response = await client.GetAsync("api/products?pageIndex=999999");

            // Assert — 200 + empty data + true total count preserved.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = await ReadRootAsync(response);
            root.GetProperty("data").GetArrayLength().Should().Be(0);
            root.GetProperty("count").GetInt32().Should().Be(18);
        }

        /// <summary>
        /// FINDING I — <c>POST api/account/login</c> with a body that OMITS the <c>password</c> field (for
        /// the seeded, resolvable e-mail) returns a structured <c>500 InternalServerError</c>. This documents
        /// a PRE-EXISTING production deviation: <c>LoginDto</c> declares no <c>[Required]</c> attributes, so
        /// <c>[ApiController]</c> model validation does NOT reject the missing password; the controller then
        /// resolves the user and calls <c>SignInManager.CheckPasswordSignInAsync(user, null, …)</c>, whose
        /// <c>PasswordHasher.VerifyHashedPassword</c> throws <c>ArgumentNullException</c> on the null password.
        /// The global <c>ExceptionMiddleware</c> surfaces it as a structured <c>ApiException</c>
        /// (<c>statusCode == 500</c> + non-empty <c>message</c>). A well-formed missing-field request would
        /// ideally be a 400; pinning the current 500 makes any future <c>[Required]</c> hardening a visible,
        /// deliberate change. The e-mail MUST be the seeded user's so the user is found (an unknown e-mail
        /// short-circuits to a 401 before the null-password path). Production code is NOT modified.
        /// </summary>
        [Fact]
        public async Task Login_MissingPasswordField_Returns500_DocumentedMissingRequiredDeviation()
        {
            // Arrange — a resolvable seeded e-mail but NO password property in the JSON body.
            var client = _fixture.CreateClient();

            // Act — the anonymous object intentionally has no "password" member => loginDto.Password == null.
            var response = await client.PostAsJsonAsync(
                "api/account/login",
                new { email = CustomWebApplicationFactory.DefaultTestUserEmail });

            // Assert — structured 500 from the null-password dereference in the identity password hasher.
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var root = await ReadRootAsync(response);
            ShouldExposeCamelCaseProperties(root, "statusCode", "message");
            root.GetProperty("statusCode").GetInt32().Should().Be(500);
            root.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }
}
