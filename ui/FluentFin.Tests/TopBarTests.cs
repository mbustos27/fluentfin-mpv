using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Controls;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Shortcuts;
using FluentFin.Core.ViewModels;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Windows.System;

namespace FluentFin.Tests;

/// <summary>
/// 1.2.0 (the top bar, replacing the left rail): the tab keys, where a page starts (below the bar, or under it for a hero),
/// the narrow-window fold, when the tabs show, and Settings and Home by their keys. The tabs themselves: TopNavigationTests;
/// the running app (Narrator's tab list, Tab, the arrows, the drag regions): tools\ui-harness\offscreen\then-topbar.ps1.
/// </summary>
public sealed class TopBarTests
{
	[Theory]
	[InlineData(VirtualKey.Right, 0, 6, 1)]
	[InlineData(VirtualKey.Right, 5, 6, 0)]
	[InlineData(VirtualKey.Left, 0, 6, 5)]
	[InlineData(VirtualKey.Left, 3, 6, 2)]
	[InlineData(VirtualKey.Home, 4, 6, 0)]
	[InlineData(VirtualKey.End, 1, 6, 5)]
	public void LeftAndRight_MoveBetweenTheTabs_Round(VirtualKey key, int at, int count, int expected)
	{
		Assert.Equal(expected, TabKeys.Next(key, at, count));
	}

	[Theory]
	[InlineData(VirtualKey.Up)]
	[InlineData(VirtualKey.Down)]
	[InlineData(VirtualKey.Tab)]
	[InlineData(VirtualKey.Enter)]
	public void OtherKeys_AreNotTheTabs(VirtualKey key)
	{
		// Tab leaves the tab list (one Tab stop); Enter and Space open the tab (the button's own)
		Assert.Null(TabKeys.Next(key, 2, 6));
	}

	[Fact]
	public void APage_StartsBelowTheBar_UnlessItExtendsUnderIt_OrTheBarIsHidden()
	{
		Assert.Equal(TopBar.Height, TopBar.TopInset(barVisible: true, extendsUnder: false));
		Assert.Equal(0, TopBar.TopInset(barVisible: true, extendsUnder: true));
		// full screen: no bar, the page (the player) fills the window
		Assert.Equal(0, TopBar.TopInset(barVisible: false, extendsUnder: false));
	}

	[Fact]
	public void TheBar_FoldsAt1100PxAndBelow()
	{
		Assert.Equal(1100, TitleBarControl.CompactWidth);
		Assert.Equal(360, TitleBarControl.SearchWidthFor(1920));
		Assert.Equal(260, TitleBarControl.SearchWidthFor(1280));
	}

	[Fact]
	public void TheTabs_ShowSignedIn_AndNotOnThePlayerPage()
	{
		var backend = new Mock<IMpvBackend>();
		backend.Setup(x => x.Events).Returns(Observable.Never<BridgeEvent>());
		backend.Setup(x => x.PlaybackInterrupted).Returns(Observable.Never<MpvInterruptedPlayback>());
		var client = new Mock<IJellyfinClient>();
		var playback = new MpvPlaybackService(backend.Object, client.Object, NullLogger<MpvPlaybackService>.Instance);
		var vm = new TitleBarViewModel(Mock.Of<INavigationService>(), Mock.Of<INavigationService>(), Mock.Of<INavigationViewService>(), client.Object, playback);
		var changed = new List<string?>();
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		Assert.False(vm.IsNavigationShown);
		vm.User = new UserDto { Name = "alice" };
		Assert.True(vm.IsNavigationShown);
		Assert.Contains(nameof(TitleBarViewModel.IsNavigationShown), changed);

		vm.OnPageShown(typeof(VideoPlayerPage));
		Assert.False(vm.IsNavigationShown);
		vm.OnPageShown(typeof(HomePage));
		Assert.True(vm.IsNavigationShown);

		vm.User = null;
		Assert.False(vm.IsNavigationShown);
	}

	[Fact]
	public void CtrlComma_OpensSettings_AndAltHome_GoesHome()
	{
		// Settings has no tab and no gear: the Account menu (TitleBarAccountTests) and Ctrl+,
		Assert.Equal(AppShortcut.OpenSettings, AppShortcuts.For(AppShortcuts.Keys.Comma, control: true, shift: false, alt: false, inTextInput: false));
		Assert.Equal(AppShortcut.GoHome, AppShortcuts.For(AppShortcuts.Keys.Home, control: false, shift: false, alt: true, inTextInput: false));
	}

	[Fact]
	public void TheClickableAreas_AreInTheWindowsPhysicalPixels()
	{
		// the bar's headers in view pixels, at 150 %: the regions Windows hit-tests are physical pixels
		var r = TitleBarControl.PassthroughRect(new Windows.Foundation.Rect(210.4, 4, 73, 40), 1.5);
		Assert.Equal((316, 6, 110, 60), (r.X, r.Y, r.Width, r.Height));
	}

	[Fact]
	public void TheBar_IsTheWindowsTitleBar_AndCtrlCommaReachesSettings()
	{
		var root = Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!;
		// without it Windows' default caption strip took the bar's top 32 px: a click on a tab dragged the window (the live
		// check: then-topbar.ps1's hit tests)
		var bar = File.ReadAllText(Path.Combine(root, "Views", "TitleBarControl.xaml.cs"));
		Assert.Contains("App.MainWindow.SetTitleBar(TitleBar);", bar);
		// and its clickable areas follow the headers (the TitleBar looks again only when the window is resized)
		Assert.Contains(".SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [.. rects]);", bar);
		Assert.Contains("foreach (var element in new FrameworkElement?[] { BackButton(), LeftHeaderPanel, RightHeaderPanel })", bar);
		var keys = File.ReadAllText(Path.Combine(root, "Services", "WindowShortcuts.cs"));
		var settings = keys[keys.IndexOf("case AppShortcut.OpenSettings:", StringComparison.Ordinal)..];
		Assert.StartsWith("case AppShortcut.OpenSettings:", settings);
		Assert.Contains("NavigateTo<SettingsViewModel>()", settings[..settings.IndexOf("case AppShortcut.GoHome", StringComparison.Ordinal)]);
	}

	[Fact]
	public void ThePluginsSectionsAndThePins_GoToTheTabs()
	{
		// the shell's navigation-item service is the tabs' (no rail to add them to): the Diary, a pinned library
		var app = File.ReadAllText(Path.Combine(Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!, "App.xaml.cs"));
		Assert.Contains("services.AddSingleton<INavigationViewServiceCore>(sp => sp.GetRequiredService<TopNavigation>());", app);
		Assert.DoesNotContain("services.AddSingleton<INavigationViewServiceCore>(sp => sp.GetRequiredService<INavigationViewService>());", app);
	}
}
