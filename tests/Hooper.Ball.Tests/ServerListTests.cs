using Hooper.Networking;

namespace Hooper.Ball.Tests;

/// <summary>
/// Tests the pure discovered-server bookkeeping (ADR-0007): dedupe by
/// (ip, gamePort), refresh-on-observe, and stale-entry expiry against an
/// injected clock. Engine-free — the real clock + socket live in
/// DiscoveryListener.
/// </summary>
public class ServerListTests
{
	private static ServerBeacon Beacon(ushort port, byte cur = 1, string name = "Court") =>
		new ServerBeacon(gamePort: port, curPlayers: cur, maxPlayers: 2, name: name);

	[Fact]
	public void Observe_adds_new_server()
	{
		var list = new ServerList();
		list.Observe("192.168.1.10", Beacon(7777), now: 0.0);

		Assert.Single(list.Servers);
		ServerListEntry e = list.Servers[0];
		Assert.Equal("192.168.1.10", e.Ip);
		Assert.Equal((ushort)7777, e.GamePort);
		Assert.Equal("Court", e.Name);
	}

	[Fact]
	public void Observe_same_key_dedupes_and_refreshes()
	{
		var list = new ServerList();
		list.Observe("192.168.1.10", Beacon(7777, cur: 1, name: "Court"), now: 0.0);
		list.Observe("192.168.1.10", Beacon(7777, cur: 2, name: "Court B"), now: 5.0);

		Assert.Single(list.Servers);
		ServerListEntry e = list.Servers[0];
		Assert.Equal(2, e.CurPlayers);     // newest payload
		Assert.Equal("Court B", e.Name);   // newest payload
		Assert.Equal(5.0, e.LastSeen);     // refreshed timestamp
	}

	[Fact]
	public void Different_game_port_same_ip_are_distinct()
	{
		var list = new ServerList();
		list.Observe("192.168.1.10", Beacon(7777), now: 0.0);
		list.Observe("192.168.1.10", Beacon(7779), now: 0.0);

		Assert.Equal(2, list.Servers.Count);
	}

	[Fact]
	public void Same_game_port_different_ip_are_distinct()
	{
		var list = new ServerList();
		list.Observe("192.168.1.10", Beacon(7777), now: 0.0);
		list.Observe("192.168.1.11", Beacon(7777), now: 0.0);

		Assert.Equal(2, list.Servers.Count);
	}

	[Fact]
	public void PruneExpired_removes_only_stale_entries()
	{
		var list = new ServerList();
		list.Observe("192.168.1.10", Beacon(7777), now: 0.0);  // will go stale
		list.Observe("192.168.1.11", Beacon(7777), now: 9.0);  // fresh

		int removed = list.PruneExpired(now: 10.0, timeoutSeconds: 3.0);

		Assert.Equal(1, removed);
		Assert.Single(list.Servers);
		Assert.Equal("192.168.1.11", list.Servers[0].Ip);
	}

	[Fact]
	public void PruneExpired_keeps_entry_refreshed_within_timeout()
	{
		var list = new ServerList();
		list.Observe("192.168.1.10", Beacon(7777), now: 0.0);
		list.Observe("192.168.1.10", Beacon(7777), now: 9.5); // refreshed just before prune

		int removed = list.PruneExpired(now: 10.0, timeoutSeconds: 3.0);

		Assert.Equal(0, removed);
		Assert.Single(list.Servers);
	}

	[Fact]
	public void PruneExpired_on_empty_list_is_noop()
	{
		var list = new ServerList();
		Assert.Equal(0, list.PruneExpired(now: 100.0, timeoutSeconds: 3.0));
		Assert.Empty(list.Servers);
	}
}
