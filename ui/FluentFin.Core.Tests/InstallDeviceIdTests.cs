namespace FluentFin.Core.Tests;

/// <summary>
/// 1.5.1 fixes (BH-150-M2): the device id Jellyfin sees is one per install and Windows user, not the PC's, so a second copy
/// (another Windows account as the same user, an installed and a portable copy, a second Nixie) signing in no longer ends
/// this copy's session; the same copy keeps its id from start to start.
/// </summary>
public sealed class InstallDeviceIdTests
{
	private const string Pc = "PC-ID-AS-DEVICEID-WRITES-IT";
	private const string Data = @"C:\Users\alice\AppData\Local\Nixie";
	private const string Program = @"C:\Users\alice\AppData\Local\Programs\Nixie\app\";
	private const string User = @"HOME\alice";

	[Fact]
	public void TheSameCopy_KeepsItsId()
	{
		Assert.Equal(InstallDeviceId.For(Pc, Data, Program, User), InstallDeviceId.For(Pc, Data, Program, User));
		// as Windows compares folders: case, a trailing separator, "." and ".." parts
		Assert.Equal(InstallDeviceId.For(Pc, Data, Program, User), InstallDeviceId.For(Pc, @"c:\users\ALICE\appdata\local\nixie\", @"C:\Users\alice\AppData\Local\Programs\Nixie\app\.\..\app", @"home\ALICE"));
	}

	[Theory]
	[InlineData("another PC", Data, Program, User)]
	[InlineData(Pc, @"D:\portable\data", Program, User)]                                     // another data folder (--data-dir)
	[InlineData(Pc, Data, @"C:\Users\alice\Downloads\Nixie-1.5.1-win-x64\", User)]             // a portable copy beside the installed one
	[InlineData(Pc, @"C:\Users\bob\AppData\Local\Nixie", Program, @"HOME\bob")]                // another Windows account
	[InlineData(Pc, Data, Program, @"HOME\bob")]
	public void AnotherCopy_IsAnotherDevice(string pc, string data, string program, string user) =>
		Assert.NotEqual(InstallDeviceId.For(Pc, Data, Program, User), InstallDeviceId.For(pc, data, program, user));

	[Fact]
	public void TheId_Is32HexDigits_AndSaysNothingOfThePcTheFoldersOrTheUser()
	{
		var id = InstallDeviceId.For(Pc, Data, Program, User);

		Assert.Matches("^[0-9a-f]{32}$", id);
		Assert.NotEqual(Pc, id);
		Assert.DoesNotContain("alice", id, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ThisCopysId_IsNotThePcs()
	{
		Assert.Matches("^[0-9a-f]{32}$", InstallDeviceId.Current);
		Assert.NotEqual(InstallDeviceId.PcId(), InstallDeviceId.Current);
		Assert.Equal(InstallDeviceId.Current, InstallDeviceId.Current);
	}
}
