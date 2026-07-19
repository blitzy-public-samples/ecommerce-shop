using Xunit;

namespace API.IntegrationTests.Infrastructure
{
    /// <summary>
    /// xUnit collection definition for the integration-test suite. It binds the shared
    /// <see cref="ContainerFixture"/> to the logical test collection named <c>"Integration"</c> so that a
    /// SINGLE set of Testcontainers instances (PostgreSQL + Redis, fronted by
    /// <see cref="CustomWebApplicationFactory"/>) is provisioned once, shared by every integration test
    /// class across the sibling folders (<c>Concurrency/</c>, <c>Caching/</c>, <c>Resilience/</c>,
    /// <c>Payments/</c>, <c>Contract/</c>, <c>Migrations/</c>, <c>Load/</c>), and disposed once after the
    /// last test in the collection.
    ///
    /// <para>
    /// Declaring <see cref="ICollectionFixture{TFixture}"/> instructs xUnit to instantiate exactly one
    /// <see cref="ContainerFixture"/> for the whole collection, invoke its
    /// <c>IAsyncLifetime.InitializeAsync</c> once before any test runs, inject that same instance into the
    /// constructor of every class annotated <c>[Collection("Integration")]</c>, and invoke
    /// <c>IAsyncLifetime.DisposeAsync</c> once after the final test — eliminating per-class container churn
    /// (AAP §0.4.4, §0.5.2, §0.5.5). The collection-name string <c>"Integration"</c> is the contract that
    /// each sibling test class references via its <c>[Collection("Integration")]</c> attribute; it must
    /// match verbatim project-wide.
    /// </para>
    ///
    /// <para>
    /// Exception: a test class that intentionally STOPS a container mid-test (for example the fail-closed
    /// resilience scenarios) must own a NON-shared <see cref="ContainerFixture"/> instance rather than
    /// joining this collection, so that tearing down infrastructure in one test cannot affect the others.
    /// </para>
    /// </summary>
    [CollectionDefinition("Integration")]
    public class IntegrationTestCollection : ICollectionFixture<ContainerFixture>
    {
        // This class has no code and is never instantiated directly.
        // Its sole purpose is to define the "Integration" test collection and
        // associate the shared ContainerFixture (started once, disposed once)
        // with every test class annotated [Collection("Integration")].
    }
}
