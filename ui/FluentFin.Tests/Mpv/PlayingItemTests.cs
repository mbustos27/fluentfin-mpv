using FluentFin.Controls;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests.Mpv;

/// <summary>1.5 (BH-140-m7): the playing item's answer (its chapters) is asked once per item, and a failed one is asked again.</summary>
public sealed class PlayingItemTests
{
	private const string Id = "0123456789abcdef0123456789abcdef";
	private const string Other = "fedcba9876543210fedcba9876543210";

	private sealed class Server
	{
		public int Asked { get; private set; }

		/// <summary>What each request does, in order; past the end, the item comes back.</summary>
		public Queue<Func<BaseItemDto?>> Answers { get; } = new();

		public Task<BaseItemDto?> Fetch(Guid id)
		{
			Asked++;
			var answer = Answers.Count > 0 ? Answers.Dequeue() : () => new BaseItemDto { Id = id, Name = "Episode" };
			return Task.FromResult(answer());
		}
	}

	[Fact]
	public async Task OneItem_IsAskedOnce()
	{
		var server = new Server();
		long now = 0;
		var item = new ItemOnce(server.Fetch, () => now);

		Assert.NotNull(await item.Get(Id));
		now += 60_000;
		Assert.NotNull(await item.Get(Id.ToUpperInvariant()));

		Assert.Equal(1, server.Asked);
	}

	[Fact]
	public async Task ARequestThatThrew_IsAskedAgain_AfterTheBackOff()
	{
		var server = new Server();
		server.Answers.Enqueue(() => throw new HttpRequestException("blip"));
		long now = 0;
		var item = new ItemOnce(server.Fetch, () => now);

		Assert.Null(await item.Get(Id));
		now += 1_000;
		Assert.Null(await item.Get(Id));
		Assert.Equal(1, server.Asked);

		now += (long)ItemOnce.RetryAfter.TotalMilliseconds;
		var again = await item.Get(Id);

		Assert.Equal("Episode", again?.Name);
		Assert.Equal(2, server.Asked);
		Assert.Same(again, await item.Get(Id));
		Assert.Equal(2, server.Asked);
	}

	[Fact]
	public async Task ANullAnswer_IsAskedAgainToo()
	{
		var server = new Server();
		server.Answers.Enqueue(() => null);
		long now = 0;
		var item = new ItemOnce(server.Fetch, () => now);

		Assert.Null(await item.Get(Id));
		now += (long)ItemOnce.RetryAfter.TotalMilliseconds;

		Assert.NotNull(await item.Get(Id));
		Assert.Equal(2, server.Asked);
	}

	[Fact]
	public async Task AnotherItem_IsAskedAtOnce_EvenRightAfterAFailure()
	{
		var server = new Server();
		server.Answers.Enqueue(() => throw new HttpRequestException("blip"));
		long now = 0;
		var item = new ItemOnce(server.Fetch, () => now);

		Assert.Null(await item.Get(Id));
		Assert.NotNull(await item.Get(Other));
		Assert.Equal(2, server.Asked);
	}

	[Fact]
	public async Task NoIdOrABadOne_AsksNothing()
	{
		var server = new Server();
		var item = new ItemOnce(server.Fetch, () => 0);

		Assert.Null(await item.Get(null));
		Assert.Null(await item.Get("not-a-guid"));
		Assert.Equal(0, server.Asked);
	}
}
