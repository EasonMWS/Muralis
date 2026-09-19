using Muralis.Desktop.Input;
using Xunit;

namespace Muralis.Desktop.Tests.Input;

public sealed class RawPointerWindowTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void OwnershipSnapshotIdentifiesAnOccupiedMouseClass(int mouseRegistrations, bool occupied)
    {
        var registration = new RawInputRegistration(mouseRegistrations, mouseRegistrations, 0);

        Assert.Equal(occupied, registration.HasMouseOwner);
    }

    [Fact]
    public void ClosingWithoutRegistrationAndRepeatedDisposeAreSafe()
    {
        var source = new RawPointerWindow();

        source.Close();
        source.Close();
        source.Dispose();
        source.Dispose();

        Assert.False(source.IsRegistered);
    }
}
