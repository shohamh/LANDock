using System.Text.Json;
using LANDock.Client;
using LANDock.Core;
using LANDock.Host;
using LANDock.Ui;

namespace LANDock.Tests;

/// <summary>The handshake between a laptop (host) and a desktop PC (client), over the real control port with HTTPS.</summary>
[Collection("paths")]
public class HandshakeTests : IAsyncLifetime
{
    private ControlServer? _server;
    private HostService? _host;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = ControlServer.ChoosePort(0);
        _host = new HostService(new Settings());
        _server = new ControlServer(_host);
        await _server.StartAsync(_port);
    }

    public async Task DisposeAsync()
    {
        if (_server != null) await _server.StopAsync();
        _host?.Dispose();
    }

    [Fact]
    public void A_free_port_is_chosen_from_the_range_and_a_configured_free_port_is_kept()
    {
        var p = ControlServer.ChoosePort(0);
        Assert.InRange(p, ControlServer.FirstPort, ControlServer.LastPort);
        var free = ControlServer.ChoosePort(p);   // free now, so it stays
        Assert.Equal(p, free);
    }

    [Fact]
    public async Task First_contact_reports_the_host_and_its_certificate_fingerprint()
    {
        var (id, fp) = await ClientService.CheckHostAsync("127.0.0.1", _port);
        Assert.Equal(HostIdentity.HostId(), id.HostId);
        Assert.Equal(HostIdentity.Fingerprint(), fp);
        Assert.False(id.PairingOpen);   // the host is not running in this test, so no pairing window
    }

    [Fact]
    public async Task A_saved_fingerprint_that_does_not_match_refuses_the_connection()
    {
        var wrong = new string('0', 64);
        var ex = await Assert.ThrowsAsync<ControlException>(() => ClientService.CheckHostAsync("127.0.0.1", _port, wrong));
        Assert.Contains("certificate", ex.Message);
        Assert.Contains("changed", ex.Message);
    }

    [Fact]
    public async Task Pairing_is_refused_while_the_host_is_not_running()
    {
        using var c = new ControlClient("127.0.0.1", _port, HostIdentity.Fingerprint());
        var ex = await Assert.ThrowsAsync<ControlException>(() => c.PairAsync("1234", "test-desktop", "0123456789ABCDEF"));
        Assert.Equal(403, ex.Status);
        Assert.Contains("not running", ex.Message);
    }

    [Fact]
    public async Task A_session_without_a_valid_token_is_refused()
    {
        using var c = new ControlClient("127.0.0.1", _port, HostIdentity.Fingerprint());
        var ex = await Assert.ThrowsAsync<ControlException>(() => c.SessionAsync("not-a-token", new[] { new DisplayOffer("M1", 1920, 1080, 60) }, "0123456789ABCDEF"));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public void Displays_are_agreed_inside_what_the_host_can_serve()
    {
        var (ok, agreed, _) = _host!.Agree(new List<StreamOffer> { new("M1", 5000, 3000, 45), new("TV", 100, 100, 24) }, "");
        Assert.True(ok);
        var m1 = agreed.Single(a => a.Name == "M1");
        Assert.Equal((3840, 2160, 60), (m1.Width, m1.Height, m1.Fps));
        var tv = agreed.Single(a => a.Name == "TV");
        Assert.Equal((640, 480, 30), (tv.Width, tv.Height, tv.Fps));
        Assert.Equal(HostPlanning.Instances.Single(i => i.Label == "M1").Port, m1.Port);
    }

    [Fact]
    public void An_unknown_display_name_is_refused()
    {
        var (ok, _, reason) = _host!.Agree(new List<StreamOffer> { new("NOPE", 1920, 1080, 60) }, "");
        Assert.False(ok);
        Assert.Contains("NOPE", reason);
    }

    [Fact]
    public void A_paired_desktop_token_is_known_until_forgotten()
    {
        var name = "test-" + Guid.NewGuid().ToString("N")[..8];
        var token = HostIdentity.Issue(name);
        Assert.True(HostIdentity.IsKnown(token));
        Assert.False(HostIdentity.IsKnown("someone-else"));
        Assert.True(HostIdentity.Forget(name));
        Assert.False(HostIdentity.IsKnown(token));
    }

    [Fact]
    public void Host_profiles_survive_a_round_trip_with_their_monitor_choices()
    {
        var s = new Settings();
        var h = new HostProfile { Name = "laptop", Address = "10.0.0.20", ControlPort = 49901, Fingerprint = "FP", HostId = "id-1" };
        h.Streams.Add(new StreamSettings { Name = "M1", Port = 49989, Monitor = 2, Resolution = "1920x1080", Fps = 60 });
        h.Streams.Add(new StreamSettings { Name = "TV", Port = 51989, Monitor = 4, Resolution = "3840x2160", Fps = 30, WindowedRect = new[] { 1, 2, 3, 4 } });
        s.Client.Hosts.Add(h);
        s.Client.DefaultHostId = h.Id;
        var file = Path.Combine(Path.GetTempPath(), "landock-profile-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            s.Save(file);
            var back = Settings.Load(file).Client.DefaultHost!;
            Assert.Equal("laptop", back.Name);
            Assert.Equal("id-1", back.HostId);
            Assert.Equal(2, back.Streams.Single(x => x.Name == "M1").Monitor);
            Assert.Equal(new[] { 1, 2, 3, 4 }, back.Streams.Single(x => x.Name == "TV").WindowedRect);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_token_is_never_written_to_the_settings_file()
    {
        var s = new Settings();
        var h = new HostProfile { Name = "laptop", Address = "10.0.0.20", Token = "secret-token-value" };
        s.Client.Hosts.Add(h);
        var file = Path.Combine(Path.GetTempPath(), "landock-token-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            s.Save(file);
            Assert.DoesNotContain("secret-token-value", File.ReadAllText(file));
            Assert.Equal("secret-token-value", Settings.Load(file).Client.Hosts[0].Token);
        }
        finally { File.Delete(file); }
    }
}
