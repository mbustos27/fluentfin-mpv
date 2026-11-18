using System.Collections.ObjectModel;
using FluentFin.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using FluentFin.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// fluentfin-mpv: the server page shows progress while it checks an address or looks for servers, gives up on an address
/// that does not answer, and says on the page why something did not work (docs/research/ux-review.md F2).
/// </summary>
public sealed class SelectServerViewModelTests
{
	private readonly ObservableCollection<SavedServer> _servers = [];
	private readonly Mock<ISettings> _settings = new();
	private readonly Mock<INavigationService> _navigation = new();
	private readonly List<string> _asked = [];

	static SelectServerViewModelTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public SelectServerViewModelTests()
	{
		_settings.SetupGet(x => x.Servers).Returns(_servers);
	}

	[Theory]
	[InlineData("", "Enter the server's address")]
	[InlineData("   ", "Enter the server's address")]
	[InlineData("jellyfin.example.com:8920", "is not a full address")]
	[InlineData("ftp://jellyfin.example.com", "is not a full address")]
	public async Task AnAddressThatIsNotOne_IsExplained_WithoutAnyRequest(string text, string expected)
	{
		var vm = Create((_, _) => throw new InvalidOperationException("no request expected"));

		await vm.CheckConnectivityAndGoToLoginCommand.ExecuteAsync(text);

		Assert.Contains(expected, vm.ErrorMessage, StringComparison.Ordinal);
		Assert.True(vm.HasError);
		Assert.Empty(_asked);
	}

	[Fact]
	public async Task WhileAnAddressIsChecked_ThePageShowsProgress_AndASecondNextWaits()
	{
		var answer = new TaskCompletionSource<(PublicSystemInfo?, string?)>();
		var vm = Create((_, _) => answer.Task);

		var checking = vm.CheckConnectivityAndGoToLoginCommand.ExecuteAsync("https://jf.example:8920");

		Assert.True(vm.IsConnecting);
		Assert.True(vm.IsBusy);
		Assert.False(vm.IsIdle);
		Assert.Equal("Connecting to jf.example:8920…", vm.StatusText);
		await vm.CheckConnectivityAndGoToLoginCommand.ExecuteAsync("https://jf.example:8920");
		Assert.Single(_asked);

		answer.SetResult((null, "Unable to reach https://jf.example:8920: refused"));
		await checking;

		Assert.False(vm.IsConnecting);
		Assert.Equal("", vm.StatusText);
		Assert.Equal("Unable to reach https://jf.example:8920: refused", vm.ErrorMessage);
		_navigation.Verify(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);
	}

	[Fact]
	public async Task AnAddressThatDoesNotAnswer_IsGivenUpOn_AfterTheTimeout()
	{
		var vm = Create(async (_, cancellationToken) =>
		{
			try
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
				return (null, "answered");
			}
			catch (OperationCanceledException)
			{
				return (null, "gave up");
			}
		});
		vm.ConnectTimeout = TimeSpan.FromMilliseconds(100);

		await vm.CheckConnectivityAndGoToLoginCommand.ExecuteAsync("https://jf.example:8920").WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("gave up", vm.ErrorMessage);
		Assert.False(vm.IsConnecting);
	}

	[Fact]
	public async Task ALookupThatIsCancelled_SaysTheServerDidNotAnswerInTime()
	{
		// cancelled before anything is sent: no request leaves this PC
		var (info, error) = await JellyfinAuthenticationService.GetPublicInfo("http://127.0.0.1:9", new CancellationToken(canceled: true));

		Assert.Null(info);
		Assert.Contains("didn't answer in time", error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AServerThatAnswers_GoesToTheLogin_AndAClearedErrorStaysCleared()
	{
		var vm = Create((_, _) => Task.FromResult<(PublicSystemInfo?, string?)>((new PublicSystemInfo { Id = "server-1", ServerName = "Home" }, null)));
		await vm.CheckConnectivityAndGoToLoginCommand.ExecuteAsync("");
		Assert.True(vm.HasError);

		await vm.CheckConnectivityAndGoToLoginCommand.ExecuteAsync("http://127.0.0.1:8096");

		Assert.False(vm.HasError);
		_navigation.Verify(x => x.NavigateTo(typeof(LoginViewModel).FullName!, It.Is<SavedServer>(s => s.Id == "server-1"), false), Times.Once);
	}

	[Fact]
	public async Task LookingForServers_ShowsProgress_AndSaysWhenNoneAnswered()
	{
		Action? finish = null;
		var vm = Create();
		vm.Discover = (_, onCompleted) =>
		{
			finish = onCompleted;
			return Task.CompletedTask;
		};

		var detecting = vm.DetectServersCommand.ExecuteAsync(null);
		Assert.True(vm.IsDetecting);
		Assert.Equal("Looking for servers on this network…", vm.StatusText);
		finish!();
		await detecting;

		Assert.False(vm.IsDetecting);
		Assert.Contains("No Jellyfin server answered", vm.ErrorMessage, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LookingForServers_ThatFindsOne_AlreadySaved_SaysNothingIsWrong()
	{
		_servers.Add(new SavedServer { Id = "server-1", DisplayName = "Home" });
		var vm = Create();
		vm.Discover = (found, onCompleted) =>
		{
			found(new DiscoveryInfo { Id = "server-1", Name = "Home", Address = "http://192.168.1.2:8096" });
			onCompleted();
			return Task.CompletedTask;
		};

		await vm.DetectServersCommand.ExecuteAsync(null);

		Assert.False(vm.HasError);
		Assert.Single(_servers);
	}

	[Fact]
	public async Task LookingForServers_ThatCannotBroadcast_StopsSpinning_AndSaysWhy()
	{
		var vm = Create();
		vm.Discover = (_, _) => throw new System.Net.Sockets.SocketException(10051);

		await vm.DetectServersCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(vm.IsDetecting);
		Assert.Contains("Couldn't look for servers on this network", vm.ErrorMessage, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheFirstVisit_LooksForServersByItself_ButNotWithServersSaved()
	{
		var looked = 0;
		var vm = Create();
		vm.Discover = (_, onCompleted) =>
		{
			looked++;
			onCompleted();
			return Task.CompletedTask;
		};

		await vm.OnShown();
		Assert.Equal(1, looked);

		_servers.Add(new SavedServer { Id = "server-1" });
		var withServers = new SelectServerViewModel(_settings.Object, _navigation.Object, TestHomeCache.Unused());
		withServers.Discover = (_, _) =>
		{
			looked++;
			return Task.CompletedTask;
		};
		await withServers.OnShown();
		Assert.Equal(1, looked);
	}

	[Fact]
	public async Task RemoveOnASavedServer_AsksNamingIt_AndOnlyAYesRemovesIt()
	{
		// 1.1.4 (states; layout audit #12): the server page's Remove asks Settings' question
		var server = new SavedServer { Id = "s1", DisplayName = "Living room" };
		_servers.Add(server);
		var vm = Create();
		var asked = new List<Core.ConfirmPrompt>();
		var answer = false;
		vm.Confirm = prompt => { asked.Add(prompt); return Task.FromResult(answer); };

		await vm.RemoveServerCommand.ExecuteAsync(server);
		Assert.Equal("Remove Living room?", Assert.Single(asked).Title);
		Assert.Equal([server], _servers);

		answer = true;
		await vm.RemoveServerCommand.ExecuteAsync(server);
		Assert.Empty(_servers);
		Assert.False(vm.HasServers);
	}

	[Fact]
	public async Task RemoveWithoutAPrompt_RemovesNothing()
	{
		var server = new SavedServer { Id = "s1", DisplayName = "Living room" };
		_servers.Add(server);

		await Create().RemoveServerCommand.ExecuteAsync(server);

		Assert.Equal([server], _servers);
	}

	private SelectServerViewModel Create(Func<string, CancellationToken, Task<(PublicSystemInfo?, string?)>>? publicInfo = null)
	{
		var vm = new SelectServerViewModel(_settings.Object, _navigation.Object, TestHomeCache.Unused())
		{
			NetworkNames = () => [],
		};
		vm.PublicInfo = (url, cancellationToken) =>
		{
			_asked.Add(url);
			return publicInfo is null ? Task.FromResult<(PublicSystemInfo?, string?)>((null, "no server")) : publicInfo(url, cancellationToken);
		};
		vm.Discover = (_, onCompleted) =>
		{
			onCompleted();
			return Task.CompletedTask;
		};
		return vm;
	}
}
