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

/// <summary>1.6.1: tests that set <see cref="InstallDeviceId.UseDataFolder"/> (static) run alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstallDeviceIdStaticCollection
{
	public const string Name = "1.6.1: InstallDeviceId's data folder (static), alone";
}

/// <summary>
/// 1.6.1 (the review): <see cref="InstallDeviceId.Current"/> never throws, whatever the window lock meets: a window of the copy
/// running as administrator holds the first slot, and a normal window's sign-in still gets an id (the second window's).
/// </summary>
[Collection(InstallDeviceIdStaticCollection.Name)]
public sealed class InstallDeviceIdCurrentTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nixie-161-device-id", Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		InstallDeviceId.UseDataFolder(null);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}

	[Fact]
	public void TheCurrentId_WithTheFirstSlotHeldByAnAdministratorsWindow_IsTheSecondWindows()
	{
		var id = InstallDeviceId.Stored(_root, InstallDeviceId.PcId(), AppContext.BaseDirectory, $@"{Environment.UserDomainName}\{Environment.UserName}");
		using var admin = StoredDeviceIdTests.HeldByAnAdministratorsWindow(id, 0);

		InstallDeviceId.UseDataFolder(_root);
		var current = InstallDeviceId.Current;

		Assert.Equal(InstallDeviceId.ForWindow(id, 1), current);
		Assert.Equal(current, InstallDeviceId.Current);
	}
}

/// <summary>
/// 1.6.1 (BH-151-m5, BH-151-m3): the id is kept in the data folder the first time, as 1.6.0 worked it out (an update is the
/// same device, so a per-device allow list keeps taking it); a new version folder or drive letter is the same device; another
/// PC or Windows user using the folder is not; two windows of one copy are two devices, the same two every time.
/// </summary>
public sealed class StoredDeviceIdTests : IDisposable
{
	private const string Pc = "PC-ID-AS-DEVICEID-WRITES-IT";
	private const string User = @"HOME\alice";
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nixie-161-device-id", Guid.NewGuid().ToString("N"));

	private string Data => Path.Combine(_root, "E", "NixieData");
	private string IdFile => Path.Combine(Data, InstallDeviceId.FileName);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}

	[Fact]
	public void TheFirstStart_KeepsTheIdThat160Sent_AndWritesIt()
	{
		var program = Path.Combine(_root, "Nixie-1.6.0-win-x64", "app");

		var id = InstallDeviceId.Stored(Data, Pc, program, User);

		Assert.Equal(InstallDeviceId.For(Pc, Data, program, User), id); // migrated, not reset
		Assert.True(File.Exists(IdFile));
		var text = File.ReadAllText(IdFile);
		Assert.Contains(id, text);
		Assert.DoesNotContain(Pc, text);          // the file says nothing of the PC's id or the user
		Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(Directory.GetFiles(Data, "*.tmp"));
	}

	[Fact]
	public void ANewVersionFolder_OrAnotherDriveLetter_IsTheSameDevice()
	{
		var id = InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "Nixie-1.6.1-win-x64", "app"), User);

		// the next portable version, unzipped beside it
		Assert.Equal(id, InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "Nixie-1.6.2-win-x64", "app"), User));
		// the USB drive with the copy and its data folder, given another letter (the folder moves with it)
		var moved = Path.Combine(_root, "F", "NixieData");
		Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
		Directory.Move(Data, moved);
		Assert.Equal(id, InstallDeviceId.Stored(moved, Pc, Path.Combine(_root, "F", "Nixie-1.6.2-win-x64", "app"), User));
	}

	/// <summary>
	/// 1.6.1 (the review): the installed and portable copies share the default data folder. The 1.6.1 zip tried first, then
	/// the installed copy updated: the installed copy keeps the id 1.6.0 sent from its folder (the device an administrator
	/// allowed), not the zip's; and a later portable version keeps the zip's.
	/// </summary>
	[Fact]
	public void TheZipFirst_ThenTheInstalledCopysUpdate_KeepsItsOwn160Id()
	{
		var installedProgram = Path.Combine(_root, "Programs", "Nixie", "app");
		var was160 = InstallDeviceId.For(Pc, Data, installedProgram, User);

		var zip = InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "Downloads", "Nixie-1.6.1-win-x64", "app"), User, installed: false);
		var installed = InstallDeviceId.Stored(Data, Pc, installedProgram, User, installed: true);

		Assert.Equal(was160, installed);
		Assert.NotEqual(zip, installed);
		Assert.Equal(zip, InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "Downloads", "Nixie-1.6.2-win-x64", "app"), User, installed: false));
		Assert.Equal(was160, InstallDeviceId.Stored(Data, Pc, installedProgram, User, installed: true));
		// the other order too
		Directory.Delete(Data, true);
		Assert.Equal(was160, InstallDeviceId.Stored(Data, Pc, installedProgram, User, installed: true));
		Assert.NotEqual(was160, InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "Downloads", "Nixie-1.6.1-win-x64", "app"), User, installed: false));
	}

	[Theory]
	[InlineData("another PC", User)]
	[InlineData(Pc, @"HOME\bob")]
	public void AnotherPcOrUser_IsAnotherDevice_AndTheFirstKeepsItsId(string pc, string user)
	{
		var program = Path.Combine(_root, "app");
		var id = InstallDeviceId.Stored(Data, Pc, program, User);

		var other = InstallDeviceId.Stored(Data, pc, program, user);

		Assert.NotEqual(id, other);
		Assert.Equal(other, InstallDeviceId.Stored(Data, pc, Path.Combine(_root, "elsewhere"), user));
		Assert.Equal(id, InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "elsewhere"), User));
	}

	[Theory]
	[InlineData("")]
	[InlineData("{\"ids\": ")]
	[InlineData("[1, 2]")]
	[InlineData("{\"version\": 1, \"ids\": [\"x\"]}")]
	public void AnUnreadableFile_IsWorkedOutAgain_AndWrittenOver(string content)
	{
		Directory.CreateDirectory(Data);
		File.WriteAllText(IdFile, content);
		var program = Path.Combine(_root, "app");

		var id = InstallDeviceId.Stored(Data, Pc, program, User);

		Assert.Equal(InstallDeviceId.For(Pc, Data, program, User), id);
		Assert.Equal(id, InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "elsewhere"), User));
	}

	[Fact]
	public void AnEntryThatIsNotAnId_IsWorkedOutAgain()
	{
		var program = Path.Combine(_root, "app");
		var id = InstallDeviceId.Stored(Data, Pc, program, User);
		File.WriteAllText(IdFile, File.ReadAllText(IdFile).Replace(id, "not-an-id"));

		Assert.Equal(id, InstallDeviceId.Stored(Data, Pc, program, User));
		Assert.DoesNotContain("not-an-id", File.ReadAllText(IdFile));
	}

	/// <summary>
	/// 1.6.1 (the review): a file that can't be read (held by another program) is not an unreadable one: this start sends the
	/// id worked out and writes nothing, so the file keeps its id and every other entry.
	/// </summary>
	[Fact]
	public void AFileThatCantBeRead_IsLeftAsItIs()
	{
		var program = Path.Combine(_root, "app");
		var other = InstallDeviceId.Stored(Data, "another PC", program, User);
		var before = File.ReadAllText(IdFile);
		// held without read sharing, but replaceable (delete sharing): a rewrite would succeed and lose the other entry
		using (new FileStream(IdFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
		{
			Assert.Equal(InstallDeviceId.For(Pc, Data, program, User), InstallDeviceId.Stored(Data, Pc, program, User));
		}

		Assert.Equal(before, File.ReadAllText(IdFile));
		Assert.Equal(other, InstallDeviceId.Stored(Data, "another PC", Path.Combine(_root, "elsewhere"), User));
		Assert.Empty(Directory.GetFiles(Data, "*.tmp"));
	}

	/// <summary>1.6.1 (the review): a write that can't replace the file leaves no .tmp behind (one was left on every start).</summary>
	[Fact]
	public void AWriteThatFails_LeavesNoTempFile()
	{
		var program = Path.Combine(_root, "app");
		InstallDeviceId.Stored(Data, "another PC", program, User);
		using (new FileStream(IdFile, FileMode.Open, FileAccess.Read, FileShare.Read)) // readable, not replaceable
		{
			Assert.Equal(InstallDeviceId.For(Pc, Data, program, User), InstallDeviceId.Stored(Data, Pc, program, User));
			Assert.Equal(InstallDeviceId.For(Pc, Data, program, User), InstallDeviceId.Stored(Data, Pc, program, User));
		}

		Assert.Empty(Directory.GetFiles(Data, "*.tmp"));
	}

	[Fact]
	public void AFolderThatCantBeWritten_StillGivesTheId()
	{
		Directory.CreateDirectory(Data);
		File.WriteAllText(IdFile, "");
		var program = Path.Combine(_root, "app");
		using (new FileStream(IdFile, FileMode.Open, FileAccess.Read, FileShare.None)) // held: no read, no replace
		{
			Assert.Equal(InstallDeviceId.For(Pc, Data, program, User), InstallDeviceId.Stored(Data, Pc, program, User));
		}
	}

	/// <summary>A window of the copy run as administrator: its slot's lock is one a normal window may not open.</summary>
	internal static IDisposable HeldByAnAdministratorsWindow(string id, int slot)
	{
		var security = new System.Security.AccessControl.MutexSecurity();
		security.SetAccessRuleProtection(true, false);
		security.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(
			new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
			System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
		var held = System.Threading.MutexAcl.Create(false, $@"Local\Nixie-device-{id}-{slot}", out var created, security);
		Assert.True(created);
		return held;
	}

	/// <summary>
	/// 1.6.1 (the review): a slot held by a window running as administrator threw an access error out of the claim (and so out
	/// of <see cref="InstallDeviceId.Current"/>, which every sign-in asks for): it is taken, and the next slot is this window's.
	/// </summary>
	[Fact]
	public void ASlotThisWindowMayNotOpen_IsTaken_AndTheNextIsClaimed()
	{
		var id = Guid.NewGuid().ToString("N");
		using var admin = HeldByAnAdministratorsWindow(id, 0);
		Assert.Throws<UnauthorizedAccessException>(() => new Mutex(false, $@"Local\Nixie-device-{id}-0", out _)); // what the claim met

		var (slot, claim) = InstallDeviceId.ClaimWindow(id);
		using (claim)
		{
			Assert.Equal(1, slot);
		}
	}

	[Fact]
	public void TwoWindowsOfOneCopy_AreTwoDevices_TheSameTwoEveryTime()
	{
		var id = Guid.NewGuid().ToString("N"); // a copy no real window runs

		var (first, firstClaim) = InstallDeviceId.ClaimWindow(id);
		var (second, secondClaim) = InstallDeviceId.ClaimWindow(id);
		try
		{
			Assert.Equal(0, first);
			Assert.Equal(1, second);
			Assert.Equal(id, InstallDeviceId.ForWindow(id, first));
			Assert.NotEqual(id, InstallDeviceId.ForWindow(id, second));
			Assert.Matches("^[0-9a-f]{32}$", InstallDeviceId.ForWindow(id, second));
			Assert.Equal(InstallDeviceId.ForWindow(id, second), InstallDeviceId.ForWindow(id, 1));
			Assert.NotEqual(InstallDeviceId.ForWindow(id, 1), InstallDeviceId.ForWindow(Guid.NewGuid().ToString("N"), 1));

			// the first window closes: the next start takes its slot, and its id, again
			firstClaim.Dispose();
			var (again, againClaim) = InstallDeviceId.ClaimWindow(id);
			using (againClaim)
			{
				Assert.Equal(0, again);
			}
		}
		finally
		{
			firstClaim.Dispose();
			secondClaim.Dispose();
		}
	}
}
