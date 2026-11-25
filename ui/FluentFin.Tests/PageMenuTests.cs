using FluentFin.Converters;
using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.7.5 (the basics gate's menus check, tools\gate\checks\3-menus.ps1): an item page's More options. For a user who
/// isn't an administrator a series' and a season's page showed it, and a click opened nothing (its menu leaves out Play and
/// the toggles, which are beside it, and has no video or administration entries for them). It shows only with something in
/// it now, in every theme's item header (JellyfinFlyoutConverter.ApplyPageMenu). Uses SessionInfo (static) and the WinUI
/// projection: runs alone.
/// </summary>
[Collection(Mpv.AloneCollection.Name)]
public sealed class PageMenuTests : IDisposable
{
	private readonly UserDto? _signedIn = SessionInfo.CurrentUser;

	public void Dispose() => SessionInfo.CurrentUser = _signedIn!;

	private static UserDto User(bool admin) => new() { Id = Guid.NewGuid(), Name = admin ? "admin" : "viewer", Policy = new UserPolicy { IsAdministrator = admin } };

	[Theory]
	[InlineData(BaseItemDto_Type.Series, false, false)]
	[InlineData(BaseItemDto_Type.Season, false, false)]
	[InlineData(BaseItemDto_Type.Movie, false, true)]   // Media info
	[InlineData(BaseItemDto_Type.Episode, false, true)] // Media info
	[InlineData(BaseItemDto_Type.Series, true, true)]   // the administrator's entries
	[InlineData(BaseItemDto_Type.Season, true, true)]
	public void AnItemPagesMoreOptions_HasSomethingInIt_ForTheUserSignedIn(BaseItemDto_Type type, bool admin, bool expected)
	{
		SessionInfo.CurrentUser = User(admin);
		Assert.Equal(expected, JellyfinFlyoutConverter.PageMenuHasItems(new BaseItemDto { Id = Guid.NewGuid(), Type = type, Name = "A Signal in Winter" }));
	}

	[Fact]
	public void NoItem_NoMenu() => Assert.False(JellyfinFlyoutConverter.PageMenuHasItems(null));

	// every item header: Classic's hero and the three themes' (Editorial's in-place details too)
	[Theory]
	[InlineData("Controls", "ItemHero.xaml.cs")]
	[InlineData("Themes", "Cinematic", "Controls", "CinematicItemStage.xaml.cs")]
	[InlineData("Themes", "DriveIn", "Controls", "ItemBoard.xaml.cs")]
	[InlineData("Themes", "Editorial", "Controls", "EditorialItemHeader.xaml.cs")]
	[InlineData("Themes", "Editorial", "Controls", "InlineDetailsRow.xaml.cs")]
	public void EveryItemHeader_SetsItsMoreOptions_SoItShowsOnlyWithSomethingInIt(params string[] parts)
	{
		var source = File.ReadAllText(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. parts])));
		Assert.Contains("JellyfinFlyoutConverter.ApplyPageMenu(MoreButton, ", source);
		Assert.DoesNotContain("MoreButton.Flyout =", source);
	}

	// a new header that sets a More options' flyout itself would bring the dead button back
	[Fact]
	public void NoOtherCode_SetsAMoreOptionsFlyout_ItsOwnWay()
	{
		var project = Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!;
		var sep = Path.DirectorySeparatorChar;
		var setters = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
			.Where(f => File.ReadAllText(f).Contains("MoreButton.Flyout ="))
			.Select(f => Path.GetRelativePath(project, f))
			.ToList();
		Assert.Empty(setters);
	}
}
