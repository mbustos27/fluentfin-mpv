using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

public sealed class FullScreenCloakTests
{
	[Fact]
	public void AnExceptionAfterTheCloak_UncloaksAndIsNotRemembered()
	{
		var cloaked = false;
		var uncloaked = 0;

		Assert.Throws<InvalidOperationException>(() => FullScreenCloak.Run(() => true, () => uncloaked++, c => cloaked = c,
			() =>
			{
				Assert.True(cloaked);                 // remembered before anything else can fail
				throw new InvalidOperationException();
			}));

		Assert.Equal(1, uncloaked);
		Assert.False(cloaked);
	}

	[Fact]
	public void WithoutAnException_TheCloakStaysForTheUncloakTimer()
	{
		var cloaked = false;
		var uncloaked = 0;

		FullScreenCloak.Run(() => true, () => uncloaked++, c => cloaked = c, () => { });

		Assert.True(cloaked);
		Assert.Equal(0, uncloaked);
	}

	[Fact]
	public void NoCloak_NothingToUndo()
	{
		var uncloaked = 0;

		Assert.Throws<InvalidOperationException>(() => FullScreenCloak.Run(() => false, () => uncloaked++, _ => { }, () => throw new InvalidOperationException()));

		Assert.Equal(0, uncloaked);
	}
}
