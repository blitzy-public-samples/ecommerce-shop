using System;
using System.Linq.Expressions;
using API.Specifications; // Production namespace quirk: Core/Specifications/*.cs declare `namespace API.Specifications`, NOT Core.Specifications.
using Core.Entities;      // Product is used as the concrete generic argument T for all tests.
using FluentAssertions;
using Xunit;

namespace Core.Tests.Specifications
{
    /// <summary>
    /// Unit tests for the specification-pattern base class
    /// <see cref="BaseSpecification{T}"/> (and, implicitly, the
    /// <c>ISpecification&lt;T&gt;</c> contract it implements).
    ///
    /// These are pure state/logic tests: the subject is constructed directly and
    /// its public read-only surface is inspected. There is no mocking, no I/O and
    /// no external infrastructure involved.
    ///
    /// Naming follows the repository convention
    /// <c>MethodName_StateUnderTest_ExpectedBehavior</c> and every test uses the
    /// Arrange-Act-Assert structure with FluentAssertions for expressive, precise
    /// failure messages.
    ///
    /// Because the mutators on <see cref="BaseSpecification{T}"/>
    /// (<c>ApplyPaging</c>, <c>AddOrderBy</c>, <c>AddOrderByDescending</c> and
    /// <c>AddInclude</c>) are declared <c>protected</c>, they cannot be invoked on
    /// a <c>BaseSpecification&lt;T&gt;</c> instance from outside its inheritance
    /// hierarchy. A private, test-only subclass (<see cref="TestableSpec{T}"/>)
    /// therefore exposes them through public pass-through methods so their
    /// behavior can be asserted.
    /// </summary>
    public class BaseSpecificationTests
    {
        /// <summary>
        /// Private test-only subclass that surfaces the <c>protected</c> mutators of
        /// <see cref="BaseSpecification{T}"/> through public pass-through methods.
        /// This mirrors how real specification subclasses (e.g.
        /// <c>ProductsWithTypesAndBrandsSpecification</c>) invoke those members from
        /// within their own constructors, without touching production code.
        /// </summary>
        /// <typeparam name="T">The entity type the specification targets.</typeparam>
        private class TestableSpec<T> : BaseSpecification<T>
        {
            /// <summary>Forwards to the parameterless base constructor (leaves <c>Criteria</c> null).</summary>
            public TestableSpec()
            {
            }

            /// <summary>Forwards to the criteria base constructor (sets the get-only <c>Criteria</c>).</summary>
            /// <param name="criteria">The filter predicate to assign to <c>Criteria</c>.</param>
            public TestableSpec(Expression<Func<T, bool>> criteria) : base(criteria)
            {
            }

            /// <summary>Public pass-through to the protected <c>ApplyPaging</c> mutator.</summary>
            public void CallApplyPaging(int skip, int take) => ApplyPaging(skip, take);

            /// <summary>Public pass-through to the protected <c>AddOrderBy</c> mutator.</summary>
            public void CallAddOrderBy(Expression<Func<T, object>> orderByExpression) => AddOrderBy(orderByExpression);

            /// <summary>Public pass-through to the protected <c>AddOrderByDescending</c> mutator.</summary>
            public void CallAddOrderByDescending(Expression<Func<T, object>> orderByDescExpression) =>
                AddOrderByDescending(orderByDescExpression);

            /// <summary>Public pass-through to the protected <c>AddInclude</c> mutator.</summary>
            public void CallAddInclude(Expression<Func<T, object>> includeExpression) => AddInclude(includeExpression);
        }

        // -----------------------------------------------------------------------
        // Constructor behavior
        // -----------------------------------------------------------------------

        [Fact]
        public void Constructor_WithCriteria_SetsCriteria()
        {
            // Arrange
            Expression<Func<Product, bool>> criteria = p => p.Id == 5;

            // Act
            var spec = new TestableSpec<Product>(criteria);

            // Assert
            spec.Criteria.Should().NotBeNull();
            // Confirm the stored expression actually captures the supplied predicate
            // by compiling and invoking it against matching and non-matching products.
            spec.Criteria.Compile().Invoke(new Product { Id = 5, Name = "x" }).Should().BeTrue();
            spec.Criteria.Compile().Invoke(new Product { Id = 6, Name = "x" }).Should().BeFalse();
        }

        [Fact]
        public void Constructor_Parameterless_LeavesCriteriaNull()
        {
            // Arrange & Act
            var spec = new TestableSpec<Product>();

            // Assert
            spec.Criteria.Should().BeNull();
        }

        // -----------------------------------------------------------------------
        // Default state of the public read-only surface
        // -----------------------------------------------------------------------

        [Fact]
        public void Includes_ByDefault_IsEmptyAndNotNull()
        {
            // Arrange & Act
            var spec = new TestableSpec<Product>();

            // Assert
            spec.Includes.Should().NotBeNull();
            spec.Includes.Should().BeEmpty();
        }

        [Fact]
        public void OrderBy_ByDefault_IsNull()
        {
            // Arrange & Act
            var spec = new TestableSpec<Product>();

            // Assert
            spec.OrderBy.Should().BeNull();
        }

        [Fact]
        public void OrderByDescending_ByDefault_IsNull()
        {
            // Arrange & Act
            var spec = new TestableSpec<Product>();

            // Assert
            spec.OrderByDescending.Should().BeNull();
        }

        [Fact]
        public void PagingProperties_ByDefault_AreZeroAndDisabled()
        {
            // Arrange & Act
            var spec = new TestableSpec<Product>();

            // Assert
            spec.Skip.Should().Be(0);
            spec.Take.Should().Be(0);
            spec.IsPagingEnabled.Should().BeFalse();
        }

        // -----------------------------------------------------------------------
        // Protected-mutator behavior (exercised via the TestableSpec pass-throughs)
        // -----------------------------------------------------------------------

        [Fact]
        public void AddInclude_WhenCalledOnce_AppendsSingleInclude()
        {
            // Arrange
            var spec = new TestableSpec<Product>();

            // Act
            spec.CallAddInclude(p => p.ProductType);

            // Assert
            spec.Includes.Should().HaveCount(1);
        }

        [Fact]
        public void AddInclude_WhenCalledTwice_AppendsBothIncludes()
        {
            // Arrange
            var spec = new TestableSpec<Product>();

            // Act
            spec.CallAddInclude(p => p.ProductType);
            spec.CallAddInclude(p => p.ProductBrand);

            // Assert
            spec.Includes.Should().HaveCount(2);
        }

        [Fact]
        public void AddOrderBy_WhenCalled_SetsOrderBy()
        {
            // Arrange
            var spec = new TestableSpec<Product>();

            // Act
            spec.CallAddOrderBy(p => p.Name);

            // Assert
            spec.OrderBy.Should().NotBeNull();
            // Strengthen the assertion: the stored selector must resolve to the Name value.
            spec.OrderBy.Compile().Invoke(new Product { Name = "abc" }).Should().Be("abc");
        }

        [Fact]
        public void AddOrderByDescending_WhenCalled_SetsOrderByDescending()
        {
            // Arrange
            var spec = new TestableSpec<Product>();

            // Act
            spec.CallAddOrderByDescending(p => p.Price);

            // Assert
            spec.OrderByDescending.Should().NotBeNull();
        }

        [Fact]
        public void ApplyPaging_WhenCalled_SetsSkipTakeAndEnablesPaging()
        {
            // Arrange
            var spec = new TestableSpec<Product>();

            // Act
            spec.CallApplyPaging(10, 20);

            // Assert
            spec.Skip.Should().Be(10);
            spec.Take.Should().Be(20);
            spec.IsPagingEnabled.Should().BeTrue();
        }
    }
}
