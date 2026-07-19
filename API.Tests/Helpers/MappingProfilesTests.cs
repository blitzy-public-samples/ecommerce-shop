using System;
using System.Collections.Generic;
using API.Dtos;
using API.Helpers;
using AutoMapper;
using Core.Entities;
using Core.Entities.OrderAggregate;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace API.Tests.Helpers
{
    /// <summary>
    /// Unit tests for the AutoMapper <see cref="MappingProfiles"/> profile and its two
    /// picture-URL value resolvers, <see cref="ProductUrlResolver"/> and
    /// <see cref="OrderItemUrlResolver"/>.
    /// <para>
    /// The suite validates two independent concerns:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <description><b>Configuration correctness</b> — every <c>CreateMap</c> declared in the
    ///     profile is internally consistent, proven by AutoMapper's
    ///     <c>AssertConfigurationIsValid()</c> not throwing.</description>
    ///   </item>
    ///   <item>
    ///     <description><b>Projection behaviour</b> — the flattening rules (brand/type name,
    ///     delivery-method short name and price, order total) and the URL-composition logic of the
    ///     two resolvers produce the expected DTO shape for both the happy path (a picture path is
    ///     present, so the configured <c>ApiUrl</c> is prefixed) and the edge path (an empty/null
    ///     picture path yields a <c>null</c> URL).</description>
    ///   </item>
    /// </list>
    /// <para>
    /// The resolvers read <c>IConfiguration["ApiUrl"]</c> and have no parameterless constructor, so
    /// the mapper is built through the service-factory overload
    /// <c>MapperConfiguration.CreateMapper(Func&lt;Type, object&gt;)</c> which supplies each resolver
    /// with a test <see cref="IConfiguration"/>. Every test constructs its own objects and its own
    /// mapper, so the class holds no shared mutable state and is safe to execute in parallel.
    /// </para>
    /// <para>
    /// This is a test-only artifact: it references the production profile and resolvers exactly as
    /// they ship and does not modify any production code (AAP §0.10.1).
    /// </para>
    /// </summary>
    public class MappingProfilesTests
    {
        /// <summary>
        /// The <c>ApiUrl</c> base value injected into the resolvers for every test. A trailing slash
        /// is intentional so the composed URL (<c>ApiUrl + picturePath</c>) reads as a well-formed
        /// absolute address, mirroring how the resolvers concatenate in production.
        /// </summary>
        private const string TestApiUrl = "https://test/";

        // ------------------------------------------------------------------
        // Harness helpers (Phase A)
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds an in-memory <see cref="IConfiguration"/> exposing the single key the resolvers
        /// consume, <c>ApiUrl</c>. Using the real <see cref="ConfigurationBuilder"/> (rather than a
        /// mock) exercises the exact indexer path the production resolvers use at runtime.
        /// </summary>
        /// <param name="apiUrl">The value to expose under the <c>ApiUrl</c> key. Defaults to
        /// <see cref="TestApiUrl"/>.</param>
        /// <returns>A fully-built configuration whose <c>["ApiUrl"]</c> indexer returns
        /// <paramref name="apiUrl"/>.</returns>
        private static IConfiguration BuildConfiguration(string apiUrl = TestApiUrl)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["ApiUrl"] = apiUrl })
                .Build();

        /// <summary>
        /// Builds an <see cref="IMapper"/> from <see cref="MappingProfiles"/> using the
        /// service-factory overload so that AutoMapper can construct the two
        /// <see cref="IValueResolver{TSource, TDestination, TDestMember}"/> implementations —
        /// neither of which has a parameterless constructor — with the supplied
        /// <see cref="IConfiguration"/>.
        /// </summary>
        /// <remarks>
        /// A plain <c>mapperConfig.CreateMapper()</c> (no factory) would fail at map time when
        /// AutoMapper tries to instantiate <see cref="ProductUrlResolver"/> /
        /// <see cref="OrderItemUrlResolver"/>, because it would look for a public parameterless
        /// constructor that does not exist. The factory returns explicitly-constructed resolver
        /// instances for those two types and falls back to <see cref="Activator.CreateInstance(Type)"/>
        /// for any other type AutoMapper might request.
        /// </remarks>
        /// <param name="config">The configuration handed to the resolvers. When <c>null</c>, a fresh
        /// configuration is created via <see cref="BuildConfiguration(string)"/>.</param>
        /// <returns>A ready-to-use mapper wired with both resolvers.</returns>
        private static IMapper BuildMapper(IConfiguration config = null)
        {
            config ??= BuildConfiguration();

            var mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfiles>());

            return mapperConfig.CreateMapper(type =>
            {
                if (type == typeof(ProductUrlResolver))
                {
                    return new ProductUrlResolver(config);
                }

                if (type == typeof(OrderItemUrlResolver))
                {
                    return new OrderItemUrlResolver(config);
                }

                return Activator.CreateInstance(type);
            });
        }

        // ------------------------------------------------------------------
        // Phase B — Configuration validity
        // ------------------------------------------------------------------

        /// <summary>
        /// Asserts that the entire profile is internally valid: every destination member of every
        /// <c>CreateMap</c> is satisfied. This exercises all map declarations at once — the two
        /// resolver-backed <c>PictureUrl</c> members, the flattened brand/type/delivery members,
        /// the <see cref="Order.GetTotal()"/> method convention, the enum-to-string status, and the
        /// <c>ReverseMap</c> leg for the identity address. Configuration validation does not
        /// instantiate the resolvers, so no service factory is required here.
        /// </summary>
        [Fact]
        public void MappingProfiles_Configuration_IsValid()
        {
            // Arrange
            var mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfiles>());

            // Act & Assert
            mapperConfig.Invoking(c => c.AssertConfigurationIsValid()).Should().NotThrow();
        }

        // ------------------------------------------------------------------
        // Phase C — Happy-path mapping (picture path present)
        // ------------------------------------------------------------------

        /// <summary>
        /// <see cref="ProductUrlResolver"/> happy path: when a product has a non-empty
        /// <c>PictureUrl</c>, the DTO's <c>PictureUrl</c> is the configured <c>ApiUrl</c> prefixed to
        /// the relative path, and the brand/type navigation properties are flattened to their
        /// <c>Name</c> strings while <c>Price</c> round-trips exactly.
        /// </summary>
        [Fact]
        public void Map_ProductToProductToReturnDto_WithPictureUrl_PrefixesApiUrlAndFlattensBrandAndType()
        {
            // Arrange
            var mapper = BuildMapper();
            var product = new Product
            {
                Id = 1,
                Name = "Test Board",
                Description = "d",
                Price = 18.5m,
                PictureUrl = "images/products/board.png",
                ProductBrand = new ProductBrand { Id = 2, Name = "Angular" },
                ProductType = new ProductType { Id = 3, Name = "Boards" }
            };

            // Act
            var dto = mapper.Map<Product, ProductToReturnDto>(product);

            // Assert
            dto.PictureUrl.Should().Be("https://test/images/products/board.png");
            dto.ProductBrand.Should().Be("Angular");
            dto.ProductType.Should().Be("Boards");
            dto.Price.Should().Be(18.5m);
        }

        /// <summary>
        /// <see cref="OrderItemUrlResolver"/> happy path: the resolver (which wins over the earlier
        /// <c>MapFrom(s =&gt; s.ItemOrdered.PictureUrl)</c> declared on the same member) prefixes the
        /// configured <c>ApiUrl</c> to the ordered item's picture path, while the ordered-item id and
        /// name are flattened from <see cref="ProductItemOrdered"/> and price/quantity round-trip.
        /// </summary>
        [Fact]
        public void Map_OrderItemToOrderItemDto_WithPictureUrl_PrefixesApiUrl()
        {
            // Arrange
            var mapper = BuildMapper();
            var orderItem = new OrderItem(
                new ProductItemOrdered(5, "Test Board", "images/products/board.png"),
                18.5m,
                2);

            // Act
            var dto = mapper.Map<OrderItem, OrderItemDto>(orderItem);

            // Assert
            dto.PictureUrl.Should().Be("https://test/images/products/board.png");
            dto.ProductId.Should().Be(5);
            dto.ProductName.Should().Be("Test Board");
            dto.Price.Should().Be(18.5m);
            dto.Quantity.Should().Be(2);
        }

        /// <summary>
        /// <see cref="Order"/> projection: the delivery method is flattened to its short name, the
        /// shipping price is taken from the delivery method, the subtotal round-trips, and
        /// <c>Total</c> is computed from <see cref="Order.GetTotal()"/> (subtotal + delivery price).
        /// The nested <c>OrderItems</c> collection is mapped element-by-element, so the child DTO's
        /// <c>PictureUrl</c> proves the <see cref="OrderItemUrlResolver"/> also runs inside the
        /// collection projection. The status enum is projected to its string form.
        /// </summary>
        [Fact]
        public void Map_OrderToOrderToReturnDto_FlattensDeliveryMethodAndComputesTotal()
        {
            // Arrange
            var mapper = BuildMapper();
            var order = new Order
            {
                Id = 1,
                BuyerEmail = "bob@test.com",
                ShipToAddress = new Address("Bob", "Smith", "10 Main St", "New York", "NY", "10001"),
                DeliveryMethod = new DeliveryMethod
                {
                    Id = 1,
                    ShortName = "UPS1",
                    DeliveryTime = "1-2 Days",
                    Description = "Fastest delivery time",
                    Price = 5m
                },
                Subtotal = 20m,
                Status = OrderStatus.Pending,
                OrderItems = new List<OrderItem>
                {
                    new OrderItem(new ProductItemOrdered(10, "Ordered Board", "images/p.png"), 20m, 1)
                }
            };

            // Act
            var dto = mapper.Map<Order, OrderToReturnDto>(order);

            // Assert
            dto.DeliveryMethod.Should().Be("UPS1");
            dto.ShippingPrice.Should().Be(5m);
            dto.Subtotal.Should().Be(20m);
            dto.Total.Should().Be(25m); // GetTotal() == Subtotal (20) + DeliveryMethod.Price (5)
            dto.BuyerEmail.Should().Be("bob@test.com");
            dto.Status.Should().Be("Pending");
            dto.OrderItems.Should().HaveCount(1);
            dto.OrderItems[0].PictureUrl.Should().Be("https://test/images/p.png");
        }

        // ------------------------------------------------------------------
        // Phase D — Null-when-empty edges (picture path absent)
        // ------------------------------------------------------------------

        /// <summary>
        /// <see cref="ProductUrlResolver"/> edge path: an empty source <c>PictureUrl</c> makes the
        /// resolver return <c>null</c> (no <c>ApiUrl</c> is prefixed) while the unrelated brand/type
        /// flattening still succeeds.
        /// </summary>
        [Fact]
        public void Map_ProductToProductToReturnDto_WithEmptyPictureUrl_ReturnsNullPictureUrl()
        {
            // Arrange
            var mapper = BuildMapper();
            var product = new Product
            {
                Id = 1,
                Name = "No Picture Board",
                Description = "d",
                Price = 10m,
                PictureUrl = string.Empty,
                ProductBrand = new ProductBrand { Id = 2, Name = "Angular" },
                ProductType = new ProductType { Id = 3, Name = "Boards" }
            };

            // Act
            var dto = mapper.Map<Product, ProductToReturnDto>(product);

            // Assert
            dto.PictureUrl.Should().BeNull();
            dto.ProductBrand.Should().Be("Angular");
            dto.ProductType.Should().Be("Boards");
        }

        /// <summary>
        /// <see cref="OrderItemUrlResolver"/> edge path: a <c>null</c> ordered-item picture path makes
        /// the resolver return <c>null</c>. The ordered item is still constructed with a non-null
        /// <see cref="ProductItemOrdered"/> because the resolver dereferences <c>ItemOrdered</c>
        /// before inspecting its picture path.
        /// </summary>
        [Fact]
        public void Map_OrderItemToOrderItemDto_WithNullPictureUrl_ReturnsNullPictureUrl()
        {
            // Arrange
            var mapper = BuildMapper();
            var orderItem = new OrderItem(
                new ProductItemOrdered(1, "x", null),
                1m,
                1);

            // Act
            var dto = mapper.Map<OrderItem, OrderItemDto>(orderItem);

            // Assert
            dto.PictureUrl.Should().BeNull();
            dto.ProductId.Should().Be(1);
            dto.ProductName.Should().Be("x");
        }
    }
}
