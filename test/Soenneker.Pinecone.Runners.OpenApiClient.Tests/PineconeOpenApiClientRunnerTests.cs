using Soenneker.TestHosts.Unit;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Pinecone.Runners.OpenApiClient.Tests;

[ClassDataSource<UnitTestHost>(Shared = SharedType.PerTestSession)]
public sealed class PineconeOpenApiClientRunnerTests : HostedUnitTest
{
    public PineconeOpenApiClientRunnerTests(UnitTestHost host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
