using FluentFin.Core;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Moq;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review A75-A77, worst offender 2): the dashboard's Restart and Shut down acted on the whole server
/// with no question, and signed the user out before sending the request (so the server refused it). Now each asks, naming the
/// server; Cancel and Escape (a "no") send nothing; a "yes" sends exactly one request, signed in, then the page says what is
/// happening (a restart is followed until the server answers again). Scan all libraries scans and says so.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class DashboardPowerTests : IAsyncLifetime
{
	private const string SystemInfoJson = """{"ServerName":"Harness","Version":"10.10.7","IsShuttingDown":false}""";

	private readonly AdminFakeServer _server = new();
	private readonly List<ConfirmPrompt> _asked = [];
	private readonly List<ServerNotice?> _notices = [];
	private DashboardViewModel _vm = null!;
	private ServerPower _power = null!;
	private Core.Services.JellyfinClient _client = null!;
	private int _waits;

	static DashboardPowerTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("GET", "/System/Info", 200, SystemInfoJson);
		_server.Respond("POST", "/System/Restart", 204);
		_server.Respond("POST", "/System/Shutdown", 204);
		_server.Respond("POST", "/Library/Refresh", 204);
		_client = await _server.SignIn();
		_power = new ServerPower(_client);
		_vm = Dashboard();
		_vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(DashboardViewModel.Notice))
			{
				_notices.Add(_vm.Notice);
			}
		};
	}

	private DashboardViewModel Dashboard() => new(_client, _power, Mock.Of<IUserInput<string>>(), Mock.Of<IContentDialogServiceCore>())
	{
		SystemInfo = new Jellyfin.Sdk.Generated.Models.SystemInfo { ServerName = "Harness" },
	};

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	private void Answer(bool yes) => _vm.Confirm = prompt =>
	{
		_asked.Add(prompt);
		return Task.FromResult(yes);
	};

	[Theory]
	[InlineData("restart")]
	[InlineData("shutdown")]
	public async Task ANo_CancelOrEscape_SendsNothing(string action)
	{
		Answer(yes: false);

		await (action == "restart" ? _vm.RestartCommand.ExecuteAsync(null) : _vm.ShutdownCommand.ExecuteAsync(null));

		var prompt = Assert.Single(_asked);
		Assert.Equal(action == "restart" ? "Restart Harness?" : "Shut down Harness?", prompt.Title);
		Assert.Equal(action == "restart" ? "Restart server" : "Shut down server", prompt.ConfirmText);
		Assert.Contains("Everyone watching will be interrupted", prompt.Message);
		Assert.Empty(_server.Changes);
		Assert.Null(_vm.Notice);
	}

	[Fact]
	public async Task NobodyToAsk_SendsNothing()
	{
		await _vm.RestartCommand.ExecuteAsync(null);
		await _vm.ShutdownCommand.ExecuteAsync(null);

		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task Restart_Confirmed_SendsOneRequest_SignedIn_ThenSaysRestarting_UntilTheServerIsBack()
	{
		Answer(yes: true);
		var busyWhileWaiting = new List<bool>();
		_power.Delay = _ =>
		{
			_waits++;
			busyWhileWaiting.Add(_vm.IsBusy && !_vm.RestartCommand.CanExecute(null) && !_vm.ShutdownCommand.CanExecute(null));
			// the server goes down at the first poll and comes back at the third
			_server.Respond("GET", "/System/Info", _waits < 3 ? 500 : 200, _waits < 3 ? "" : SystemInfoJson);
			return Task.CompletedTask;
		};

		await _vm.RestartCommand.ExecuteAsync(null);

		var restart = Assert.Single(_server.Changes);
		Assert.Equal("POST", restart.Method);
		Assert.Equal("/System/Restart", restart.Path);
		Assert.True(restart.SignedIn);
		Assert.DoesNotContain(_server.Requests, x => x.Path.Equals("/Sessions/Logout", StringComparison.OrdinalIgnoreCase));
		Assert.Equal([ServerPowerState.Restarting, ServerPowerState.Back], _notices.Select(x => x!.State));
		Assert.True(_notices[0]!.Busy);
		Assert.Equal("Restarting Harness…", _notices[0]!.Title);
		Assert.Equal(3, _waits);
		Assert.All(busyWhileWaiting, Assert.True);
		Assert.False(_vm.IsBusy);
		Assert.True(_vm.RestartCommand.CanExecute(null));
	}

	[Fact]
	public async Task Restart_TheServerNeverComesBack_SaysSo_AfterAFewMinutes()
	{
		Answer(yes: true);
		_power.Delay = _ =>
		{
			_waits++;
			_server.Respond("GET", "/System/Info", 500);
			return Task.CompletedTask;
		};

		await _vm.RestartCommand.ExecuteAsync(null);

		Assert.Single(_server.Changes);
		Assert.Equal(ServerPower.PollsBeforeGivingUp, _waits);
		Assert.Equal(ServerPowerState.NotBack, _vm.Notice!.State);
		Assert.False(_vm.IsBusy);
	}

	[Theory]
	[InlineData("restart", "/System/Restart")]
	[InlineData("shutdown", "/System/Shutdown")]
	public async Task Refused_SaysNothingChanged_AndWaitsForNothing(string action, string path)
	{
		Answer(yes: true);
		_server.Respond("POST", path, 500);
		_power.Delay = _ => { _waits++; return Task.CompletedTask; };

		await (action == "restart" ? _vm.RestartCommand.ExecuteAsync(null) : _vm.ShutdownCommand.ExecuteAsync(null));

		Assert.Single(_server.Changes);
		Assert.Equal(ServerPowerState.Failed, _vm.Notice!.State);
		Assert.Contains("Nothing changed", _vm.Notice.Message);
		Assert.Equal(0, _waits);
		Assert.False(_vm.IsBusy);
	}

	[Fact]
	public async Task ShutDown_Confirmed_SendsOneRequest_SignedIn_ThenSaysItIsShuttingDown()
	{
		Answer(yes: true);

		await _vm.ShutdownCommand.ExecuteAsync(null);

		var shutdown = Assert.Single(_server.Changes);
		Assert.Equal("/System/Shutdown", shutdown.Path);
		Assert.True(shutdown.SignedIn);
		Assert.DoesNotContain(_server.Requests, x => x.Path.Equals("/Sessions/Logout", StringComparison.OrdinalIgnoreCase));
		Assert.Equal(ServerPowerState.ShutDown, _vm.Notice!.State);
		Assert.Equal("Harness is shutting down", _vm.Notice.Title);
		// the server is off: nothing more to send from here, even with the notice's bar closed
		Assert.False(_vm.RestartCommand.CanExecute(null));
		Assert.False(_vm.StartScanCommand.CanExecute(null));
		_vm.DismissNotice();
		Assert.Equal(ServerPowerState.ShutDown, _vm.Notice!.State);
		Assert.False(_vm.RestartCommand.CanExecute(null));
	}

	[Fact]
	public async Task ScanAllLibraries_SendsOneRefresh_AndSaysItStarted()
	{
		await _vm.StartScanCommand.ExecuteAsync(null);

		var scan = Assert.Single(_server.Changes);
		Assert.Equal("POST", scan.Method);
		Assert.Equal("/Library/Refresh", scan.Path);
		Assert.Equal("Scan started", _vm.Notice!.Title);
	}

	/// <summary>
	/// Review fix: the dashboard's view model is new on each visit; leaving during "Restarting…" and coming back enabled Restart
	/// again while the first wait ran, and a second restart could be sent. The restart lives in ServerPower, which outlives it.
	/// </summary>
	[Fact]
	public async Task ComingBackDuringARestart_ShowsIt_AndCannotSendAnother()
	{
		Answer(yes: true);
		var held = new TaskCompletionSource();
		var waits = 0;
		_power.Delay = async _ =>
		{
			if (++waits == 1)
			{
				await held.Task; // the first wait holds: the restart is under way
			}
		};
		var first = _vm.RestartCommand.ExecuteAsync(null);
		await Task.Delay(200);
		_vm.OnNavigatedFrom().Wait();

		var again = Dashboard();
		again.Confirm = _vm.Confirm;

		Assert.Equal(ServerPowerState.Restarting, again.Notice!.State);
		Assert.True(again.IsBusy);
		Assert.False(again.RestartCommand.CanExecute(null));
		Assert.False(again.ShutdownCommand.CanExecute(null));
		await again.RestartCommand.ExecuteAsync(null);
		Assert.Single(_server.Changes);

		held.SetResult();
		await first;
		Assert.Equal(ServerPowerState.Back, _power.Notice!.State);
		Assert.Equal(ServerPowerState.Back, again.Notice!.State);
		Assert.Single(_server.Changes);
	}
}
