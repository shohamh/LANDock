using LANDock.Discovery;

namespace LANDock.Tests;

/// <summary>mDNS on this machine: a laptop advertised on the LAN must be found by a browser, with its id, name, port and what it heard.</summary>
[Trait("Category", "Network")]
public class LanTests
{
    [Fact]
    public async Task A_laptop_advertised_on_the_lan_is_found_by_a_browser()
    {
        var hostId = Guid.NewGuid().ToString("D");
        var desktopId = "0123456789ABCDEF";
        using var advertiser = new LanAdvertiser();
        using var browser = new LanBrowser(desktopId);
        advertiser.Start(new Advert(hostId, "test-laptop", "9.9.9", 49955, "open",
            new List<(string, double, double)> { (desktopId, 14.0, 2.0) }));
        browser.Start();

        DiscoveredLaptop? found = null;
        for (var i = 0; i < 40 && found == null; i++)
        {
            await Task.Delay(250);
            found = browser.Snapshot().FirstOrDefault(d => d.HostId == hostId);
        }
        Assert.NotNull(found);
        Assert.Equal(49955, found!.ControlPort);
        Assert.Equal("test-laptop", found.Name);
        Assert.Equal("open", found.Pairing);
        Assert.NotNull(found.HeardUsAt);
        Assert.Equal(14.0, found.HeardUsDb, 0);
    }
}
