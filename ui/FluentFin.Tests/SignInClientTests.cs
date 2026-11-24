using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeviceId;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.Contracts.Services;
using FluentFin.UI.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// Nixie (1.2.0): the client every sign-in uses (the server page's check, Quick Connect, a password, and the automatic
/// sign-in of a saved user at every start; all through JellyfinAuthenticationService's one client) tells the server
/// Client="Nixie", with the same device id Seaglass sent (the Windows device's). A saved sign-in is the user name and a
/// DPAPI-protected password, not a token: the automatic sign-in sends them again, so the server sees the same device sign in
/// as Nixie and gives it a new token. (174-signin: a Quick Connect sign-in has no password; its protected token is kept
/// instead, QuickConnectRememberedTests.) A fake server on localhost records the requests; no real server is ever asked.
/// </summary>
[Collection(SavedSignInCollection.Name)] // 1.6.1: SessionInfo's saved sign-in (and InstallDeviceId's data folder) are static
public sealed class SignInClientTests : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly List<(string Path, string? Authorization, string Body)> _requests = [];
	private readonly string _baseUrl;
	private readonly Task _loop;
	private volatile string? _allowedDevice;

	public SignInClientTests()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		_baseUrl = $"http://localhost:{port}";
		_listener.Prefixes.Add(_baseUrl + "/");
		_listener.Start();
		_loop = Task.Run(Serve);
	}

	private async Task Serve()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception)
			{
				return;
			}

			var path = context.Request.Url!.AbsolutePath;
			using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
			var body = await reader.ReadToEndAsync();
			lock (_requests)
			{
				_requests.Add((path, context.Request.Headers["Authorization"] ?? context.Request.Headers["X-Emby-Authorization"], body));
			}

			var signIn = path.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase);
			var answer = signIn
				? """{"AccessToken":"a-new-token-for-nixie","ServerId":"fake-server","User":{"Id":"aaaaaaaa000000000000000000000003","Name":"alice"}}"""
				: """{"ServerName":"fake","Version":"10.11.0","Id":"fake-server","StartupWizardCompleted":true}""";
			var status = 200;
			// 1.6.1 (BH-151-m5): an administrator's per-device allow list (Jellyfin's EnableAllDevices off, EnabledDevices):
			// a sign-in from any other device is refused, as Jellyfin refuses it ("not allowed on this device", 403)
			var header = context.Request.Headers["Authorization"] ?? context.Request.Headers["X-Emby-Authorization"] ?? "";
			if (signIn && _allowedDevice is { } allowed && (!Fields(header).TryGetValue("DeviceId", out var device) || device != allowed))
			{
				status = 403;
				answer = "User is not allowed access from this device.";
			}

			var bytes = Encoding.UTF8.GetBytes(answer);
			context.Response.StatusCode = status;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = bytes.Length;
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
		}
	}

	public void Dispose()
	{
		_listener.Stop();
		_listener.Close();
		_loop.Wait(TimeSpan.FromSeconds(2));
	}

	private static Dictionary<string, string> Fields(string header) =>
		Regex.Matches(header, "(\\w+)=\"([^\"]*)\"").ToDictionary(m => m.Groups[1].Value, m => Uri.UnescapeDataString(m.Groups[2].Value));

	private static string WindowsDeviceId => new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString();

	/// <summary>1.5.1 fixes (BH-150-M2): the device id every sign-in sends: this copy's, not the PC's (<see cref="WindowsDeviceId"/>).</summary>
	private static string ThisCopysDeviceId => InstallDeviceId.Current;

	[Fact]
	public async Task TheSignInClient_SaysNixie_OnTheSameDevice()
	{
		var (info, error) = await JellyfinAuthenticationService.GetPublicInfo(_baseUrl);

		Assert.Null(error);
		Assert.Equal("fake", info?.ServerName);
		string? header;
		lock (_requests)
		{
			header = Assert.Single(_requests).Authorization;
		}

		Assert.NotNull(header);
		var fields = Fields(header);
		Assert.Equal("Nixie", fields["Client"]);
		Assert.Equal(ThisCopysDeviceId, fields["DeviceId"]);
		Assert.NotEqual(WindowsDeviceId, fields["DeviceId"]);
		Assert.Equal(Environment.MachineName, fields["Device"]);
		Assert.Equal(AppVersion.Current, fields["Version"]);
		Assert.DoesNotContain("Seaglass", header);
	}

	[Fact]
	public async Task ASavedSignIn_SignsInAgain_WithItsUserNameAndPassword_AsNixie_OnTheSameDevice()
	{
		// a sign-in Seaglass saved (and the data folder copy brought over with its entropy): a user name and a protected password
		var entropy = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
		var settings = new Mock<ILocalSettingsService>();
		settings.Setup(x => x.GetEntropyBytes()).Returns(entropy);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Initialize(It.IsAny<string>(), It.IsAny<Jellyfin.Sdk.Generated.Models.AuthenticationResult>())).Returns(Task.CompletedTask);
		var titleBar = new Mock<ITitleBarViewModel>();
		titleBar.SetupAllProperties();
		var service = new JellyfinAuthenticationService(client.Object, titleBar.Object, settings.Object, Mock.Of<INavigationService>(),
			NullLogger<JellyfinAuthenticationService>.Instance);
		var server = new SavedServer { DisplayName = "home", PublicUrl = _baseUrl, LocalUrl = _baseUrl, Id = "fake-server" };
		var user = new SavedUser { Username = "alice", Password = "s3cret".Protect(entropy) };

		try
		{
			Assert.True(await service.Authenticate(server, user));
		}
		finally
		{
			SessionInfo.CurrentUserCrendentials = null!; // static: no other test sees this user
		}

		(string Path, string? Authorization, string Body) request;
		lock (_requests)
		{
			request = Assert.Single(_requests, r => r.Path.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase));
		}

		using (var body = JsonDocument.Parse(request.Body))
		{
			Assert.Equal("alice", body.RootElement.GetProperty("Username").GetString());
			Assert.Equal("s3cret", body.RootElement.GetProperty("Pw").GetString());
		}

		Assert.NotNull(request.Authorization);
		var fields = Fields(request.Authorization);
		Assert.Equal("Nixie", fields["Client"]);
		Assert.Equal(ThisCopysDeviceId, fields["DeviceId"]);
		Assert.Equal(Environment.MachineName, fields["Device"]);
		Assert.False(fields.ContainsKey("Token")); // nothing saved is sent but the user name and password
		// the session the server made for Nixie is the one the app goes on with
		client.Verify(x => x.Initialize(_baseUrl, It.Is<Jellyfin.Sdk.Generated.Models.AuthenticationResult>(a => a.AccessToken == "a-new-token-for-nixie")), Times.Once);
	}

	/// <summary>
	/// 1.5.1 fixes (BH-150-M2): the device id changed from the PC's to this copy's, and the update asks nothing: the settings
	/// file Nixie 1.5.0 wrote (a saved server and user, a protected password, the entropy; no device id anywhere) is read as it
	/// is, its saved sign-in (the start signs in by itself with one server and one user: DefaultActivationHandler) signs in with
	/// no question, now from this copy's device, and the file keeps the server and the user as they were.
	/// </summary>
	[Fact]
	public async Task ASettingsFileFrom150_SignsInWithoutAsking_AsThisCopysDevice_AndKeepsItsSignIn()
	{
		var folder = Path.Combine(Path.GetTempPath(), "nixie-151-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			var entropy = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
			var password = "s3cret".Protect(entropy);
			// LocalSettings.json as 1.5.0 writes it (indented, the saved sign-ins under "Servers", byte arrays in base64)
			var file = Path.Combine(folder, KnownFolders.SettingsFileName);
			File.WriteAllText(file, $$"""
				{
				  "Entropy": "{{Convert.ToBase64String(entropy)}}",
				  "Servers": [
				    {
				      "DisplayName": "home",
				      "PublicUrl": "{{_baseUrl}}",
				      "LocalUrl": "{{_baseUrl}}",
				      "LocalNetworkNames": [],
				      "Id": "fake-server",
				      "Users": [ { "Username": "alice", "Password": "{{Convert.ToBase64String(password)}}" } ]
				    }
				  ],
				  "MediaPlayerType": "Mpv",
				  "CheckForUpdates": true
				}
				""");
			var before = File.ReadAllText(file);
			var localSettings = new FluentFin.Core.Services.LocalSettingsService(new KnownFolders(folder));
			var server = Assert.Single(new FluentFin.Core.Settings.Settings(localSettings).Servers);
			var user = Assert.Single(server.Users);

			var client = new Mock<IJellyfinClient>();
			client.Setup(x => x.Initialize(It.IsAny<string>(), It.IsAny<Jellyfin.Sdk.Generated.Models.AuthenticationResult>())).Returns(Task.CompletedTask);
			var titleBar = new Mock<ITitleBarViewModel>();
			titleBar.SetupAllProperties();
			var service = new JellyfinAuthenticationService(client.Object, titleBar.Object, localSettings, Mock.Of<INavigationService>(),
				NullLogger<JellyfinAuthenticationService>.Instance);
			try
			{
				Assert.True(await service.Authenticate(server, user));
			}
			finally
			{
				SessionInfo.CurrentUserCrendentials = null!; // static: no other test sees this user
			}

			(string Path, string? Authorization, string Body) request;
			lock (_requests)
			{
				request = Assert.Single(_requests, r => r.Path.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase));
			}

			using (var body = JsonDocument.Parse(request.Body))
			{
				Assert.Equal("alice", body.RootElement.GetProperty("Username").GetString());
				Assert.Equal("s3cret", body.RootElement.GetProperty("Pw").GetString());
			}

			var fields = Fields(request.Authorization!);
			Assert.Equal(ThisCopysDeviceId, fields["DeviceId"]);
			Assert.NotEqual(WindowsDeviceId, fields["DeviceId"]);
			client.Verify(x => x.Initialize(_baseUrl, It.Is<Jellyfin.Sdk.Generated.Models.AuthenticationResult>(a => a.AccessToken == "a-new-token-for-nixie")), Times.Once);

			// nothing reset: the saved server and user are still there, as they were
			var after = new FluentFin.Core.Settings.Settings(new FluentFin.Core.Services.LocalSettingsService(new KnownFolders(folder)));
			Assert.Equal("alice", Assert.Single(Assert.Single(after.Servers).Users).Username);
			using var was = JsonDocument.Parse(before);
			using var now = JsonDocument.Parse(File.ReadAllText(file));
			Assert.Equal(was.RootElement.GetProperty("Servers").GetRawText(), now.RootElement.GetProperty("Servers").GetRawText());
			Assert.Equal(was.RootElement.GetProperty("Entropy").GetString(), now.RootElement.GetProperty("Entropy").GetString());
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	/// <summary>
	/// 1.6.1 (BH-151-m5): the id 1.6.0 sent, worked out as 1.6.0 did (a frozen copy: <see cref="InstallDeviceId.For"/> may not
	/// drift from it, or every update becomes a new device).
	/// </summary>
	private static string IdThat160Sent(string dataFolder, string programFolder)
	{
		static string Folder(string f) => Path.GetFullPath(f).TrimEnd('\\', '/').ToUpperInvariant();
		var text = string.Join('\n', "nixie-device-id-1", WindowsDeviceId, Folder(dataFolder), Folder(programFolder),
			$@"{Environment.UserDomainName}\{Environment.UserName}".ToUpperInvariant());
		return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);
	}

	/// <summary>The start's automatic sign-in (DefaultActivationHandler's), called directly: a launch's arguments can't be made outside WinUI.</summary>
	private sealed class StartSignIn(INavigationService setup, FluentFin.Core.Settings.ISettings settings, IJellyfinAuthenticationService auth)
		: FluentFin.Activation.DefaultActivationHandler(setup, Mock.Of<INavigationService>(), settings, Mock.Of<ILocalSettingsService>(),
			Mock.Of<IPluginManager>(), auth, Mock.Of<IMainWindowViewModel>(), NullLogger<FluentFin.Activation.DefaultActivationHandler>.Instance)
	{
		public Task Run() => HandleInternalAsync(null!);
	}

	/// <summary>
	/// 1.6.1 (BH-151-m5): the device id is now kept in the data folder, and the update from 1.6.0 asks nothing and stays the
	/// same device. The data folder 1.6.0 left (LocalSettings.json with a saved server, user and protected password; no
	/// device-id.json) is started on by the start's own automatic sign-in, against a server whose administrator allows this user
	/// only 1.6.0's device: it signs in without asking (the shell opens, not the server page), with 1.6.0's id, keeps that id in
	/// the data folder for the next start, and leaves the saved sign-in as it was.
	/// </summary>
	[Fact]
	public async Task ASettingsFileFrom160_SignsInWithoutAsking_AsTheSameDevice_AndKeepsTheId()
	{
		var folder = Path.Combine(Path.GetTempPath(), "nixie-161-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			var entropy = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
			var password = "s3cret".Protect(entropy);
			// LocalSettings.json as 1.6.0 writes it (the saved sign-ins under "Servers", byte arrays in base64, 1.6's settings beside them)
			var file = Path.Combine(folder, KnownFolders.SettingsFileName);
			File.WriteAllText(file, $$"""
				{
				  "Entropy": "{{Convert.ToBase64String(entropy)}}",
				  "Servers": [
				    {
				      "DisplayName": "home",
				      "PublicUrl": "{{_baseUrl}}",
				      "LocalUrl": "{{_baseUrl}}",
				      "LocalNetworkNames": [],
				      "Id": "fake-server",
				      "Users": [ { "Username": "alice", "Password": "{{Convert.ToBase64String(password)}}" } ]
				    }
				  ],
				  "MediaPlayerType": "Mpv",
				  "CheckForUpdates": true,
				  "TidyCollectionNames": true,
				  "QualityAtHome": "Original",
				  "ShowPlaybackStatsButton": false
				}
				""");
			var before = File.ReadAllText(file);
			var was160 = IdThat160Sent(folder, AppContext.BaseDirectory);
			_allowedDevice = was160;
			Assert.False(File.Exists(Path.Combine(folder, InstallDeviceId.FileName)));

			// the start, as App's: the data folder first, then the automatic sign-in of the one saved user
			InstallDeviceId.UseDataFolder(folder);
			var localSettings = new FluentFin.Core.Services.LocalSettingsService(new KnownFolders(folder));
			var settings = new FluentFin.Core.Settings.Settings(localSettings);
			var client = new Mock<IJellyfinClient>();
			client.Setup(x => x.Initialize(It.IsAny<string>(), It.IsAny<Jellyfin.Sdk.Generated.Models.AuthenticationResult>())).Returns(Task.CompletedTask);
			var titleBar = new Mock<ITitleBarViewModel>();
			titleBar.SetupAllProperties();
			var setup = new Mock<INavigationService>();
			var service = new JellyfinAuthenticationService(client.Object, titleBar.Object, localSettings, setup.Object,
				NullLogger<JellyfinAuthenticationService>.Instance);
			try
			{
				await new StartSignIn(setup.Object, settings, service).Run().WaitAsync(TimeSpan.FromSeconds(30));
				Assert.Equal(was160, InstallDeviceId.Current);
				// BH-151-m3: this window holds the copy's first slot, so a second window of the copy started now is another device
				var (second, claim) = InstallDeviceId.ClaimWindow(was160);
				using (claim)
				{
					Assert.Equal(1, second);
					Assert.NotEqual(was160, InstallDeviceId.ForWindow(was160, second));
				}
			}
			finally
			{
				SessionInfo.CurrentUserCrendentials = null!; // static: no other test sees this user
				InstallDeviceId.UseDataFolder(null);           // and none sees this data folder's id
			}

			// signed in without asking: the shell, never the server page
			setup.Verify(x => x.NavigateTo("FluentFin.ViewModels.ShellViewModel", It.IsAny<object?>(), It.IsAny<bool>()), Times.Once);
			setup.Verify(x => x.NavigateTo("FluentFin.Core.ViewModels.SelectServerViewModel", It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);
			(string Path, string? Authorization, string Body) request;
			lock (_requests)
			{
				request = Assert.Single(_requests, r => r.Path.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase));
			}

			Assert.Equal(was160, Fields(request.Authorization!)["DeviceId"]);
			client.Verify(x => x.Initialize(_baseUrl, It.Is<Jellyfin.Sdk.Generated.Models.AuthenticationResult>(a => a.AccessToken == "a-new-token-for-nixie")), Times.Once);

			// the id is kept for the next start, whatever folder the program then runs from (a new portable version)
			Assert.Contains(was160, File.ReadAllText(Path.Combine(folder, InstallDeviceId.FileName)));
			Assert.Equal(was160, InstallDeviceId.Stored(folder, WindowsDeviceId, Path.Combine(folder, "Nixie-1.6.2-win-x64", "app"),
				$@"{Environment.UserDomainName}\{Environment.UserName}"));

			// nothing reset: the saved server and user are still there, as they were
			using var then = JsonDocument.Parse(before);
			using var now = JsonDocument.Parse(File.ReadAllText(file));
			Assert.Equal(then.RootElement.GetProperty("Servers").GetRawText(), now.RootElement.GetProperty("Servers").GetRawText());
			Assert.Equal(then.RootElement.GetProperty("Entropy").GetString(), now.RootElement.GetProperty("Entropy").GetString());
		}
		finally
		{
			_allowedDevice = null;
			Directory.Delete(folder, true);
		}
	}

	/// <summary>
	/// 1.6.1 (BH-151-m5): the allow list in the fake above refuses another device, so the test above would fail on an id that
	/// moved (a new device after the update).
	/// </summary>
	[Fact]
	public async Task TheAllowList_RefusesAnotherDevice()
	{
		_allowedDevice = "0123456789abcdef0123456789abcdef";
		try
		{
			var entropy = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
			var settings = new Mock<ILocalSettingsService>();
			settings.Setup(x => x.GetEntropyBytes()).Returns(entropy);
			var service = new JellyfinAuthenticationService(Mock.Of<IJellyfinClient>(), Mock.Of<ITitleBarViewModel>(), settings.Object,
				Mock.Of<INavigationService>(), NullLogger<JellyfinAuthenticationService>.Instance);
			var server = new SavedServer { DisplayName = "home", PublicUrl = _baseUrl, LocalUrl = _baseUrl, Id = "fake-server" };

			Assert.False(await service.Authenticate(server, new SavedUser { Username = "alice", Password = "s3cret".Protect(entropy) }));
		}
		finally
		{
			_allowedDevice = null;
			SessionInfo.CurrentUserCrendentials = null!;
		}
	}
}
