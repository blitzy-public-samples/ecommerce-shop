using API.Specifications;
using Core.Entities;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Specifications
{
    /// <summary>
    /// Unit tests for <see cref="ProductsWithTypesAndBrandsSpecification"/>, the richest of the
    /// product specifications. This spec exposes two constructors:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     a <c>ProductSpecParams</c> constructor that wires up the search/brand/type
    ///     <c>Criteria</c>, adds the <c>ProductType</c> and <c>ProductBrand</c> includes, applies an
    ///     unconditional <c>OrderBy(Name)</c>, enables paging, and finally applies an optional sort
    ///     switch (priceAsc / priceDesc / default); and
    ///   </item>
    ///   <item>
    ///     a single-id constructor that only sets an <c>Id</c> equality <c>Criteria</c> plus the two
    ///     includes (no ordering, no paging).
    ///   </item>
    /// </list>
    ///
    /// These are pure-logic tests: no mocking is required. The specification's public members
    /// (inherited from <see cref="BaseSpecification{T}"/>) are inspected directly, and the captured
    /// expression trees are compiled and invoked to prove which member each selector targets.
    ///
    /// IMPORTANT NAMESPACE QUIRK: <see cref="ProductsWithTypesAndBrandsSpecification"/> and
    /// <c>ProductSpecParams</c> physically live in the Core project but declare
    /// <c>namespace API.Specifications</c>, hence the <c>using API.Specifications;</c> import above.
    ///
    /// Naming follows the repository convention MethodName_StateUnderTest_ExpectedBehavior and each
    /// test is structured as Arrange-Act-Assert with FluentAssertions.
    /// </summary>
    public class ProductsWithTypesAndBrandsSpecificationTests
    {
        /// <summary>
        /// Builds a fully-populated, non-null <see cref="Product"/> for use as test input. A non-null
        /// <c>Name</c> is mandatory because the params-constructor criteria invokes
        /// <c>x.Name.ToLower()</c>; supplying explicit brand/type/id/price keeps each assertion
        /// unambiguous.
        /// </summary>
        private static Product MakeProduct(string name, int brandId, int typeId, int id = 1, decimal price = 10m)
            => new Product
            {
                Id = id,
                Name = name,
                ProductBrandId = brandId,
                ProductTypeId = typeId,
                Price = price
            };

        // ---------------------------------------------------------------------------------------
        // Phase 1 — Params constructor: includes and paging
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ParamsConstructor_Always_AddsProductTypeAndProductBrandIncludes()
        {
            // Arrange
            var p = new ProductSpecParams();

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert
            spec.Includes.Should().NotBeNull();
            spec.Includes.Should().HaveCount(2);
        }

        [Fact]
        public void ParamsConstructor_Always_EnablesPaging()
        {
            // Arrange
            var p = new ProductSpecParams();

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert
            spec.IsPagingEnabled.Should().BeTrue();
        }

        [Fact]
        public void ParamsConstructor_WithPageIndexTwo_ComputesSkipAndTake()
        {
            // Arrange
            var p = new ProductSpecParams { PageIndex = 2, PageSize = 6 };

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert — Skip = PageSize * (PageIndex - 1) = 6 * (2 - 1) = 6; Take = PageSize = 6
            spec.Skip.Should().Be(6);
            spec.Take.Should().Be(6);
        }

        [Fact]
        public void ParamsConstructor_WithFirstPage_SkipIsZero()
        {
            // Arrange
            var p = new ProductSpecParams { PageIndex = 1, PageSize = 10 };

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert — first page: Skip = 10 * (1 - 1) = 0; Take = 10 (boundary)
            spec.Skip.Should().Be(0);
            spec.Take.Should().Be(10);
        }

        [Fact]
        public void ParamsConstructor_WithPageSizeAboveMax_TakeIsCappedAtFifty()
        {
            // Arrange — ProductSpecParams caps PageSize at MaxPageSize (50)
            var p = new ProductSpecParams { PageSize = 100 };

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert — Take capped at 50; PageIndex defaults to 1 so Skip = 50 * 0 = 0 (boundary)
            spec.Take.Should().Be(50);
            spec.Skip.Should().Be(0);
        }

        // ---------------------------------------------------------------------------------------
        // Phase 2 — Params constructor: sort branches (one test per branch for precise assertions)
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ParamsConstructor_WithPriceAscSort_OrderBySelectsPrice()
        {
            // Arrange
            var p = new ProductSpecParams { Sort = "priceAsc" };

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert — the "priceAsc" branch OVERWRITES the unconditional OrderBy(Name) with Price;
            // OrderByDescending is never set.
            spec.OrderBy.Should().NotBeNull();
            spec.OrderByDescending.Should().BeNull();
            spec.OrderBy.Compile().Invoke(MakeProduct("zzz", 1, 1, price: 42m)).Should().Be(42m);
        }

        [Fact]
        public void ParamsConstructor_WithPriceDescSort_OrderByDescendingSelectsPrice()
        {
            // Arrange
            var p = new ProductSpecParams { Sort = "priceDesc" };

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert — the "priceDesc" branch sets OrderByDescending to Price.
            spec.OrderByDescending.Should().NotBeNull();
            spec.OrderByDescending.Compile().Invoke(MakeProduct("zzz", 1, 1, price: 42m)).Should().Be(42m);

            // NUANCE: OrderBy is deliberately NOT null here — the unconditional AddOrderBy(x => x.Name)
            // runs before the switch and is not cleared by the priceDesc branch. Asserting NotBeNull
            // documents this behavior and guards against a false "OrderBy should be null" expectation.
            spec.OrderBy.Should().NotBeNull();
        }

        [Fact]
        public void ParamsConstructor_WithNoSort_OrderBySelectsName()
        {
            // Arrange — Sort is null by default, so the sort switch is skipped entirely and the
            // unconditional OrderBy(Name) remains in effect.
            var p = new ProductSpecParams();

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert
            spec.OrderBy.Should().NotBeNull();
            spec.OrderByDescending.Should().BeNull();
            spec.OrderBy.Compile().Invoke(MakeProduct("widget", 1, 1)).Should().Be("widget");
        }

        [Fact]
        public void ParamsConstructor_WithUnknownSort_OrderBySelectsName()
        {
            // Arrange — a non-empty, unrecognized Sort value drives the switch's default case,
            // which re-applies OrderBy(Name).
            var p = new ProductSpecParams { Sort = "somethingElse" };

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert
            spec.OrderBy.Should().NotBeNull();
            spec.OrderByDescending.Should().BeNull();
            spec.OrderBy.Compile().Invoke(MakeProduct("widget", 1, 1)).Should().Be("widget");
        }

        // ---------------------------------------------------------------------------------------
        // Phase 3 — Params constructor: criteria filters
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ParamsConstructor_WithNoFilters_CriteriaMatchesAnyProduct()
        {
            // Arrange — no Search/BrandId/TypeId supplied, so every clause short-circuits to true.
            var p = new ProductSpecParams();

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);

            // Assert
            spec.Criteria.Should().NotBeNull();
            spec.Criteria.Compile().Invoke(MakeProduct("anything", 3, 4)).Should().BeTrue();
        }

        [Fact]
        public void ParamsConstructor_WithSearchBrandType_CriteriaFiltersCorrectly()
        {
            // Arrange — all three filters active; Search is lower-cased by the setter.
            var p = new ProductSpecParams { Search = "board", BrandId = 1, TypeId = 2 };

            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(p);
            var criteria = spec.Criteria.Compile();

            // Assert — matching product satisfies name-contains, brand and type.
            criteria.Invoke(MakeProduct("Skate Board", 1, 2)).Should().BeTrue();

            // A product whose name lacks the search term fails the criteria even with matching
            // brand and type.
            criteria.Invoke(MakeProduct("React Book", 1, 2)).Should().BeFalse();
        }

        // ---------------------------------------------------------------------------------------
        // Phase 4 — Single-id constructor
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void IdConstructor_SetsCriteriaMatchingThatId()
        {
            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(5);
            var criteria = spec.Criteria.Compile();

            // Assert — criteria matches the requested id and rejects any other id.
            spec.Criteria.Should().NotBeNull();
            criteria.Invoke(MakeProduct("n", 1, 1, id: 5)).Should().BeTrue();
            criteria.Invoke(MakeProduct("n", 1, 1, id: 6)).Should().BeFalse();
        }

        [Fact]
        public void IdConstructor_AddsProductTypeAndProductBrandIncludes()
        {
            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(5);

            // Assert
            spec.Includes.Should().HaveCount(2);
        }

        [Fact]
        public void IdConstructor_DoesNotEnablePagingOrOrdering()
        {
            // Act
            var spec = new ProductsWithTypesAndBrandsSpecification(5);

            // Assert — the id constructor only sets criteria + includes; no paging or ordering.
            spec.IsPagingEnabled.Should().BeFalse();
            spec.OrderBy.Should().BeNull();
            spec.OrderByDescending.Should().BeNull();
        }
    }
}
