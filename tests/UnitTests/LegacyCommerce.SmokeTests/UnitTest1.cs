namespace LegacyCommerce.SmokeTests;
public sealed class FoundationTests
{
    [Fact]
    public void HostAssemblyContainsEntryPoint()
    {
        Assert.NotNull(typeof(Program).Assembly.EntryPoint);
    }
}
