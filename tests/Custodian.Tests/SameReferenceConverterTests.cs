using System.Globalization;
using Custodian.App.Controls;

namespace Custodian.Tests;

public sealed class SameReferenceConverterTests
{
    [Fact]
    public void MatchesOnlyTheSameObject()
    {
        var item = new object();
        var converter = SameReferenceConverter.Instance;

        Assert.Equal(true, converter.Convert([item, item], typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert([item, new object()], typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert([null, null], typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert([item], typeof(bool), null, CultureInfo.InvariantCulture));
    }
}
