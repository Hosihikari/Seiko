using Seiko.CommandLine;
using Xunit;

namespace Seiko.Tests;

// ADR-0034 requires every language to carry every key with matching
// placeholders; a missing key would otherwise fall back to English silently.
public sealed class LocCatalogTests
{
    [Fact]
    public void EveryLanguageHasEveryKeyAndPlaceholder()
    {
        Assert.Empty(Loc.ValidateCatalogs());
    }
}
