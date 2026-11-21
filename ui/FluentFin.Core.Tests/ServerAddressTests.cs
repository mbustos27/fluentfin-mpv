using System.Net;
using FluentFin.Core.Mpv;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.5 qacheck (the outside-services hunt's sub-path and trailing-slash cases): the address a user types, with a reverse
/// proxy's sub-path and a "/" at its end, and the URLs built from it: Nixie's own joins (<see cref="ServerAddress"/>, the
/// item pages' and Cinematic's art; Home's hero through Flurl; the server a play session belongs to), and the Jellyfin
/// SDK's, pinned for an upgrade. tools\qa-servers\subpath-probe.ps1 checks the whole app the same way on a real server.
/// </summary>
public sealed class ServerAddressTests
{
	public static TheoryData<string, string> Typed => new()
	{
		{ "http://127.0.0.1:8096", "http://127.0.0.1:8096" },
		{ "http://127.0.0.1:8096/", "http://127.0.0.1:8096" },
		{ "https://host.example/jellyfin", "https://host.example/jellyfin" },
		{ "https://host.example/jellyfin/", "https://host.example/jellyfin" },
		{ " https://host.example/jellyfin// ", "https://host.example/jellyfin" },
	};

	[Theory]
	[MemberData(nameof(Typed))]
	public void Join_KeepsTheSubPath_AndAddsNoDoubleSlash(string typed, string root)
	{
		Assert.Equal($"{root}/Items/abc/Images/Primary?fillWidth=300", ServerAddress.Join(typed, "/Items/abc/Images/Primary?fillWidth=300")!.ToString());
		Assert.Equal($"{root}/System/Info/Public", ServerAddress.Join(typed, "System/Info/Public")!.ToString());
	}

	[Theory]
	[InlineData(null, "/Items/x")]
	[InlineData("", "/Items/x")]
	[InlineData("https://host.example", null)]
	[InlineData("not an address", "/Items/x")]
	public void Join_IsNullWithoutAnAddressOrAPath(string? typed, string? path) => Assert.Null(ServerAddress.Join(typed, path));

	[Theory]
	[MemberData(nameof(Typed))]
	public void TheItemPagesArt_IsJoinedThere(string typed, string root)
	{
		// ItemArtworkConverters and CinematicArt load ItemArtwork's paths through ServerAddress.Join
		var id = Guid.NewGuid();
		var dto = new BaseItemDto { Id = id, BackdropImageTags = ["tag1"] };
		var path = ItemArtwork.BackdropPath(dto, 1280);
		Assert.NotNull(path);
		var uri = ServerAddress.Join(typed, path)!.ToString();
		Assert.StartsWith($"{root}/Items/", uri, StringComparison.Ordinal);
		Assert.DoesNotContain("//Items", uri, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("http://127.0.0.1:8096/", "http://127.0.0.1:8096")]
	[InlineData("https://host.example/jellyfin", "https://host.example/jellyfin")]
	[InlineData("https://host.example/jellyfin/", "https://host.example/jellyfin")]
	public void HomesHero_KeepsTheSubPath(string typed, string root)
	{
		// Flurl's AppendPathSegment: one "/" at the end is fine ("//" typed is ServerAddress.Join's case only)
		var id = Guid.NewGuid();
		var slide = new HeroSlide(new BaseItemDto { Id = id }, "", "", "", false, id, "t", id, "l");
		Assert.StartsWith($"{root}/Items/{id}/Images/Backdrop?", slide.BackdropUri(typed.Trim(), 1920).ToString(), StringComparison.Ordinal);
		Assert.StartsWith($"{root}/Items/{id}/Images/Logo?", slide.LogoUri(typed.Trim(), 600)!.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void APlaySession_IsTheSameServer_WithOrWithoutTheSlash_ButNotAnotherSubPath()
	{
		Assert.True(MpvPlaybackService.SameServer("https://host.example/jellyfin/", "https://host.example/jellyfin"));
		Assert.True(MpvPlaybackService.SameServer("http://127.0.0.1:8096/", "http://127.0.0.1:8096"));
		Assert.False(MpvPlaybackService.SameServer("https://host.example/jellyfin", "https://host.example/emby"));
		Assert.False(MpvPlaybackService.SameServer("https://host.example/jellyfin", "https://host.example:8920/jellyfin"));
	}

	private sealed class Capture : HttpMessageHandler
	{
		public List<Uri> Seen { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Seen.Add(request.RequestUri!);
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"Id":"x","ServerName":"QA","Version":"12.4.0"}""", System.Text.Encoding.UTF8, "application/json"),
			});
		}
	}

	[Theory]
	[MemberData(nameof(Typed))]
	public async Task TheSdk_KeepsTheSubPath_AndAddsNoDoubleSlash(string typed, string root)
	{
		var capture = new Capture();
		var settings = new Jellyfin.Sdk.JellyfinSdkSettings();
		settings.Initialize("Nixie", "1.5.0", "test", "test-device");
		settings.SetServerUrl(typed.Trim());
		var api = new Jellyfin.Sdk.JellyfinApiClient(new Jellyfin.Sdk.JellyfinRequestAdapter(new Jellyfin.Sdk.JellyfinAuthenticationProvider(settings), settings, new HttpClient(capture)));

		await api.System.Info.Public.GetAsync();

		Assert.Equal($"{root}/System/Info/Public", Assert.Single(capture.Seen).ToString());
	}
}
