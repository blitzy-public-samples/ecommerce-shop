using System;
using System.Linq.Expressions;
using Core.Entities;
// NOTE (namespace quirk): the production specification classes live physically under
// Core/Specifications/*.cs but declare `namespace API.Specifications`. The subject under
// test (ProductWithFiltersForCountSpecification) and ProductSpecParams are therefore
// imported from API.Specifications even though they ship inside the Core assembly.
using API.Specifications;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Specifications
{
    /// <summary>
    /// Unit tests for <see cref="ProductWithFiltersForCountSpecification"/> — the
    /// <em>criteria-only</em> specification used to count products that match the incoming
    /// <see cref="ProductSpecParams"/> filters. The specification passes a single predicate
    /// to <c>BaseSpecification&lt;Product&gt;</c> and deliberately configures no includes,
    /// no ordering and no paging.
    ///
    /// <para>
    /// These are pure-logic tests: the specification is constructed directly and its public
    /// <c>Criteria</c> expression is compiled and invoked against in-memory <see cref="Product"/>
    /// instances (no Moq, no database, no HTTP). Each test builds fresh arrange data so the
    /// class is safe to execute in parallel with the rest of the suite.
    /// </para>
    ///
    /// <para>
    /// The predicate under test is:
    /// <code>
    /// x =>
    ///   (string.IsNullOrEmpty(Search) || x.Name.ToLower().Contains(Search)) &amp;&amp;
    ///   (!BrandId.HasValue           || x.ProductBrandId == BrandId)         &amp;&amp;
    ///   (!TypeId.HasValue            || x.ProductTypeId  == TypeId)
    /// </code>
    /// so every filter is optional, each optional filter short-circuits to <c>true</c> when
    /// absent, and the three clauses are combined with a top-level logical AND.
    /// </para>
    ///
    /// <para>
    /// GOTCHA: <see cref="ProductSpecParams.Search"/>'s setter executes <c>value.ToLower()</c>,
    /// so assigning <c>Search = null</c> throws a <see cref="NullReferenceException"/>. Tests
    /// either leave <c>Search</c> unset (its backing field defaults to <c>null</c>, which the
    /// criteria handles safely via <c>string.IsNullOrEmpty</c>) or assign a non-null string
    /// (which is lowercased on assignment).
    /// </para>
    /// </summary>
    public class ProductWithFiltersForCountSpecificationTests
    {
        /// <summary>
        /// Builds a fully-populated <see cref="Product"/> for use as arrange data. A non-null
        /// <paramref name="name"/> is mandatory because the specification's criteria calls
        /// <c>x.Name.ToLower()</c>; passing a null name would throw when the predicate runs.
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

        // -----------------------------------------------------------------------------------
        // Phase 1 — Criteria branch tests
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Constructor_WithNoFilters_CriteriaMatchesAnyProduct()
        {
            // Arrange — no filters supplied: Search left unset (backing field null), Brand/Type null.
            var productParams = new ProductSpecParams();

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);

            // Assert — all three optional clauses short-circuit to true, so any product matches.
            spec.Criteria.Should().NotBeNull();
            spec.Criteria.Compile().Invoke(MakeProduct("anything", brandId: 1, typeId: 1)).Should().BeTrue();
        }

        [Fact]
        public void Constructor_WithSearch_CriteriaMatchesProductWhoseNameContainsSearch()
        {
            // Arrange — "Angular" is lowercased to "angular" by the setter.
            var productParams = new ProductSpecParams { Search = "Angular" };

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);

            // Assert — "Angular Board".ToLower() = "angular board" contains "angular".
            spec.Criteria.Compile().Invoke(MakeProduct("Angular Board", brandId: 1, typeId: 1)).Should().BeTrue();
        }

        [Fact]
        public void Constructor_WithSearch_CriteriaRejectsProductWhoseNameDoesNotContainSearch()
        {
            // Arrange
            var productParams = new ProductSpecParams { Search = "Angular" };

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);

            // Assert — "React Board".ToLower() = "react board" does not contain "angular".
            spec.Criteria.Compile().Invoke(MakeProduct("React Board", brandId: 1, typeId: 1)).Should().BeFalse();
        }

        [Fact]
        public void Constructor_WithBrandId_CriteriaMatchesOnlyThatBrand()
        {
            // Arrange
            var productParams = new ProductSpecParams { BrandId = 1 };

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);
            var criteria = spec.Criteria.Compile();

            // Assert — matching brand passes, non-matching brand is rejected (Type/Search unconstrained).
            criteria.Invoke(MakeProduct("x", brandId: 1, typeId: 5)).Should().BeTrue();
            criteria.Invoke(MakeProduct("x", brandId: 2, typeId: 5)).Should().BeFalse();
        }

        [Fact]
        public void Constructor_WithTypeId_CriteriaMatchesOnlyThatType()
        {
            // Arrange
            var productParams = new ProductSpecParams { TypeId = 2 };

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);
            var criteria = spec.Criteria.Compile();

            // Assert — matching type passes, non-matching type is rejected (Brand/Search unconstrained).
            criteria.Invoke(MakeProduct("x", brandId: 5, typeId: 2)).Should().BeTrue();
            criteria.Invoke(MakeProduct("x", brandId: 5, typeId: 3)).Should().BeFalse();
        }

        [Fact]
        public void Constructor_WithAllFilters_CriteriaRequiresEveryFilterToMatch()
        {
            // Arrange — every filter is populated; "board" stays lowercase.
            var productParams = new ProductSpecParams { Search = "board", BrandId = 1, TypeId = 2 };

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);
            var criteria = spec.Criteria.Compile();

            // Assert — a product satisfying all three filters matches...
            criteria.Invoke(MakeProduct("Skate Board", brandId: 1, typeId: 2)).Should().BeTrue();

            // ...while failing exactly one filter (here the brand) rejects the product,
            // confirming the top-level AND across the three clauses.
            criteria.Invoke(MakeProduct("Skate Board", brandId: 9, typeId: 2)).Should().BeFalse();
        }

        // -----------------------------------------------------------------------------------
        // Phase 2 — Structural invariants ("count spec is criteria-only")
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Constructor_Always_HasNoIncludes()
        {
            // Arrange
            var productParams = new ProductSpecParams();

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);

            // Assert — the count specification adds no eager-loading includes.
            spec.Includes.Should().BeEmpty();
        }

        [Fact]
        public void Constructor_Always_HasNoOrdering()
        {
            // Arrange
            var productParams = new ProductSpecParams();

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);

            // Assert — neither ascending nor descending ordering is configured.
            spec.OrderBy.Should().BeNull();
            spec.OrderByDescending.Should().BeNull();
        }

        [Fact]
        public void Constructor_Always_DoesNotEnablePaging()
        {
            // Arrange
            var productParams = new ProductSpecParams();

            // Act
            var spec = new ProductWithFiltersForCountSpecification(productParams);

            // Assert — paging is never applied, so the full matching set is counted.
            spec.IsPagingEnabled.Should().BeFalse();
        }
    }
}
