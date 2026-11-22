using FluentFin.Core.Outside;
using FluentFin.Core.Services;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.7 trending: the user's own TMDb API Read Access Token, kept with Windows DPAPI in the settings file (never the token
/// itself on disk), read back, cleared, and turned away when it isn't a Read Access Token's shape.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TmdbTokenTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "NixieTmdbTokenTests", Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}

	private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJuaXhpZSI6Imhhcm5lc3MifQ.bml4aWUtaGFybmVzcy1zaWduYXR1cmU";

	[Fact]
	public void TheToken_IsKeptEncrypted_ReadBack_AndSurvivesARestart()
	{
		var settings = new LocalSettingsService(new KnownFolders(_root));
		var store = new TmdbToken(settings);
		var changed = 0;
		store.Changed += (_, _) => changed++;

		Assert.False(store.HasToken);
		Assert.Null(store.Save("  " + Token + "  "));
		Assert.Equal(Token, store.Read());
		Assert.Equal(1, changed);

		// on disk: DPAPI bytes, never the token or a recognisable part of it
		var file = File.ReadAllText(Path.Combine(_root, KnownFolders.SettingsFileName));
		Assert.DoesNotContain(Token, file);
		Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", file);
		Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Token)), file);
		Assert.Contains(TmdbToken.SettingName, file);

		Assert.Equal(Token, new TmdbToken(new LocalSettingsService(new KnownFolders(_root))).Read());
		Assert.Equal("TMDb token: kept", store.ToString());
	}

	[Fact]
	public void ABadToken_IsNotKept_AndBlankClearsIt()
	{
		var store = new TmdbToken(new LocalSettingsService(new KnownFolders(_root)));
		Assert.Null(store.Save(Token));

		Assert.NotNull(store.Save("0123456789abcdef0123456789abcdef"));
		Assert.Equal(Token, store.Read());   // the good one stays

		Assert.Null(store.Save("   "));
		Assert.Null(store.Read());
		Assert.False(store.HasToken);
		Assert.Equal("TMDb token: none", store.ToString());
		Assert.Null(new TmdbToken(new LocalSettingsService(new KnownFolders(_root))).Read());
	}

	[Fact]
	public void AnotherUsersOrABrokenEntry_ReadsAsNoToken()
	{
		var settings = new LocalSettingsService(new KnownFolders(_root));
		settings.SaveSetting(TmdbToken.SettingName, new byte[] { 1, 2, 3, 4, 5 });   // not DPAPI's: another account's, another PC's
		Assert.Null(new TmdbToken(settings).Read());
		settings.SaveSetting(TmdbToken.SettingName, "not bytes at all");
		Assert.Null(new TmdbToken(settings).Read());
	}
}
