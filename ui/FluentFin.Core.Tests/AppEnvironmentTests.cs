using FluentFin.Core;

namespace FluentFin.Core.Tests;

/// <summary>Nixie: the app's own environment variables are NIXIE_*, and the SEAGLASS_* names of 1.1.x still work.</summary>
public sealed class AppEnvironmentTests
{
	private static Func<string, string?> Env(params (string Name, string Value)[] set) =>
		name => set.FirstOrDefault(x => x.Name == name).Value;

	[Fact]
	public void TheNewNameIsRead()
	{
		Assert.Equal("Light", AppIdentity.ReadEnvironment("THEME", Env(("NIXIE_THEME", "Light"))));
		Assert.Equal("cloak", AppIdentity.ReadEnvironment("FULLSCREEN", Env(("NIXIE_FULLSCREEN", "cloak"))));
	}

	[Fact]
	public void TheOldNameStillWorks()
	{
		Assert.Equal("Dark", AppIdentity.ReadEnvironment("THEME", Env(("SEAGLASS_THEME", "Dark"))));
		Assert.Equal("none", AppIdentity.ReadEnvironment("FULLSCREEN", Env(("SEAGLASS_FULLSCREEN", "none"))));
	}

	[Fact]
	public void TheNewNameWins_WhenBothAreSet()
	{
		Assert.Equal("Light", AppIdentity.ReadEnvironment("THEME", Env(("SEAGLASS_THEME", "Dark"), ("NIXIE_THEME", "Light"))));
	}

	[Fact]
	public void AnEmptyNewName_FallsBackToTheOldOne_AndNeitherIsNull()
	{
		Assert.Equal("Dark", AppIdentity.ReadEnvironment("THEME", Env(("NIXIE_THEME", ""), ("SEAGLASS_THEME", "Dark"))));
		Assert.Null(AppIdentity.ReadEnvironment("THEME", Env()));
		Assert.Null(AppIdentity.ReadEnvironment("THEME", Env(("THEME", "Light"), ("FLUENTFIN_THEME", "Light"))));
	}

	[Fact]
	public void ThePrefixes()
	{
		Assert.Equal("NIXIE_", AppIdentity.EnvironmentPrefix);
		Assert.Equal(["SEAGLASS_"], AppIdentity.FormerEnvironmentPrefixes);
		Assert.Equal(["Seaglass", "FluentFin MPV"], AppIdentity.FormerNames);
	}
}
