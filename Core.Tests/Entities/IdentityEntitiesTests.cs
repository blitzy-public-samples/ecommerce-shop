using Core.Entities.Identity;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Entities
{
    /// <summary>
    /// Pure xUnit unit tests (no mocking) for the identity domain entities
    /// <see cref="AppUser"/> and <see cref="Address"/> in the
    /// <c>Core.Entities.Identity</c> namespace.
    /// <para>
    /// The entities are instantiated directly and exercised for property
    /// round-trips, the bidirectional <see cref="AppUser"/>&#8596;
    /// <see cref="Address"/> navigation linkage, and the inherited
    /// <c>IdentityUser</c> members (for example the string <c>Id</c>,
    /// <c>UserName</c> and <c>Email</c>). These tests contribute toward the
    /// &#8805;70% Core-entities line-coverage target defined in the Agent
    /// Action Plan (&#167;0.7.1).
    /// </para>
    /// <remarks>
    /// This file is intentionally scoped to <c>Core.Entities.Identity</c> only.
    /// A distinct <c>Address</c> type also exists in
    /// <c>Core.Entities.OrderAggregate</c>; that namespace is deliberately not
    /// imported so the <c>Address</c> identifier resolves unambiguously to the
    /// identity <see cref="Address"/> entity. Each test constructs fresh
    /// objects and shares no mutable state, so the class is safe to run in
    /// parallel with the rest of the suite.
    /// </remarks>
    public class IdentityEntitiesTests
    {
        /// <summary>
        /// Verifies that an <see cref="AppUser"/>'s own properties
        /// (<see cref="AppUser.DisplayName"/>, <see cref="AppUser.Address"/>)
        /// together with the inherited <c>IdentityUser</c> members
        /// (<c>Id</c>, <c>UserName</c>, <c>Email</c>) round-trip through the
        /// object initializer. Note that the inherited <c>Id</c> is a
        /// <see cref="string"/> (not an integer), so it is asserted with a
        /// string literal.
        /// </summary>
        [Fact]
        public void AppUser_Properties_RoundTrip()
        {
            // Arrange
            var address = new Address
            {
                Id = 1,
                FirstName = "John",
                LastName = "Doe",
                Street = "10 Main St",
                City = "New York",
                State = "NY",
                ZipCode = "10001",
                AppUserId = "user-1"
            };

            // Act
            var user = new AppUser
            {
                Id = "user-1",
                UserName = "john",
                Email = "john@test.com",
                DisplayName = "John Doe",
                Address = address
            };

            // Assert
            user.Id.Should().Be("user-1");
            user.UserName.Should().Be("john");
            user.Email.Should().Be("john@test.com");
            user.DisplayName.Should().Be("John Doe");
            user.Address.Should().BeSameAs(address);
        }

        /// <summary>
        /// Verifies that every <see cref="Address"/> property round-trips
        /// through the object initializer, including the integer
        /// <see cref="Address.Id"/>, the string address fields, the required
        /// <see cref="Address.AppUserId"/> foreign key, and the
        /// <see cref="Address.AppUser"/> navigation reference.
        /// </summary>
        [Fact]
        public void Address_Properties_RoundTrip()
        {
            // Arrange
            var user = new AppUser { Id = "user-2", DisplayName = "Jane Smith" };

            // Act
            var address = new Address
            {
                Id = 5,
                FirstName = "Jane",
                LastName = "Smith",
                Street = "20 Oak Ave",
                City = "Los Angeles",
                State = "CA",
                ZipCode = "90001",
                AppUserId = "user-2",
                AppUser = user
            };

            // Assert
            address.Id.Should().Be(5);
            address.FirstName.Should().Be("Jane");
            address.LastName.Should().Be("Smith");
            address.Street.Should().Be("20 Oak Ave");
            address.City.Should().Be("Los Angeles");
            address.State.Should().Be("CA");
            address.ZipCode.Should().Be("90001");
            address.AppUserId.Should().Be("user-2");
            address.AppUser.Should().BeSameAs(user);
        }

        /// <summary>
        /// Verifies the bidirectional navigation linkage between an
        /// <see cref="AppUser"/> and its <see cref="Address"/>: assigning each
        /// side to the other results in both ends referencing the same
        /// instances (asserted with <c>BeSameAs</c>), and the linked values are
        /// consistent across the relationship.
        /// </summary>
        [Fact]
        public void AppUser_Address_LinksToAddress()
        {
            // Arrange
            var user = new AppUser
            {
                Id = "user-3",
                UserName = "alice",
                Email = "alice@test.com",
                DisplayName = "Alice Walker"
            };
            var address = new Address
            {
                Id = 9,
                FirstName = "Alice",
                LastName = "Walker",
                Street = "30 Pine Rd",
                City = "Seattle",
                State = "WA",
                ZipCode = "98101",
                AppUserId = "user-3"
            };

            // Act - establish the bidirectional navigation linkage
            user.Address = address;
            address.AppUser = user;

            // Assert - both ends reference the same instances and stay consistent
            user.Address.Should().BeSameAs(address);
            address.AppUser.Should().BeSameAs(user);
            user.Address.AppUserId.Should().Be(user.Id);
            address.AppUser.DisplayName.Should().Be("Alice Walker");
        }
    }
}
