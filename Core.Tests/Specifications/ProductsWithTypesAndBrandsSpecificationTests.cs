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

        /// <summary>
        /// Builds a <see cref="Product"/> whose <c>ProductType</c> and <c>ProductBrand</c> reference
        /// navigations are set to the supplied DISTINCT instances. Compiling an include selector and
        /// invoking it against this product returns the exact navigation the selector targets, which
        /// lets a test pin which member each include projects — not merely how many includes exist.
        /// </summary>
        private static Product MakeProductWithNavigations(ProductType productType, ProductBrand productBrand)
            => new Product
            {
                Id = 1,
                Name = "sample",
                ProductType = productType,
                ProductTypeId = productType.Id,
                ProductBrand = productBrand,
                ProductBrandId = productBrand.Id
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

            // Assert — exactly two includes, registered in order: [0] ProductType, [1] ProductBrand.
            spec.Includes.Should().NotBeNull();
            spec.Includes.Should().HaveCount(2);
            // Compile each include selector and invoke it against a product carrying DISTINCT
            // ProductType/ProductBrand instances. This pins the exact navigation each selector targets:
            // a count-only assertion would still pass if both includes selected ProductType (silently
            // dropping ProductBrand) or if the two selectors were reversed — either of which would break
            // API DTO navigation loading. BeSameAs against distinct instances catches both mistakes.
            var productType = new ProductType { Id = 7, Name = "Boards" };
            var productBrand = new ProductBrand { Id = 9, Name = "Angular" };
            var product = MakeProductWithNavigations(productType, productBrand);
            spec.Includes[0].Compile().Invoke(product).Should().BeSameAs(productType);
            spec.Includes[1].Compile().Invoke(product).Should().BeSameAs(productBrand);
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

            // NUANCE: OrderBy is deliberately NOT cleared by the priceDesc branch — the unconditional
            // AddOrderBy(x => x.Name) runs before the sort switch and remains in effect. Assert that the
            // retained OrderBy still SELECTS Name (compile + invoke), not merely that it is non-null, so
            // a regression that repointed the retained OrderBy at a different member (or accidentally set
            // it to Price) would be caught here.
            spec.OrderBy.Should().NotBeNull();
            spec.OrderBy.Compile().Invoke(MakeProduct("widget", 1, 1)).Should().Be("widget");
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
            // brand and type (Search conjunct fails).
            criteria.Invoke(MakeProduct("React Book", 1, 2)).Should().BeFalse();

            // Wrong brand: name matches and type matches, but the brand differs -> the BrandId conjunct
            // must fail. Without this case an omitted/short-circuited brand clause would go undetected
            // while count and returned rows silently diverge.
            criteria.Invoke(MakeProduct("Skate Board", 99, 2)).Should().BeFalse();

            // Wrong type: name matches and brand matches, but the type differs -> the TypeId conjunct
            // must fail. Guards the type clause the same way the wrong-brand case guards the brand clause.
            criteria.Invoke(MakeProduct("Skate Board", 1, 99)).Should().BeFalse();
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

            // Assert — the id constructor registers the SAME two includes, in the same order, as the
            // params constructor: [0] ProductType, [1] ProductBrand. Pin each selector via compile +
            // invoke against distinct instances so a wrong/duplicate/reordered include is caught.
            spec.Includes.Should().HaveCount(2);
            var productType = new ProductType { Id = 7, Name = "Boards" };
            var productBrand = new ProductBrand { Id = 9, Name = "Angular" };
            var product = MakeProductWithNavigations(productType, productBrand);
            spec.Includes[0].Compile().Invoke(product).Should().BeSameAs(productType);
            spec.Includes[1].Compile().Invoke(product).Should().BeSameAs(productBrand);
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
