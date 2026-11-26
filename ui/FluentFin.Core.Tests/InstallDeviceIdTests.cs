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

	/// <summary>
	/// 1.7.5 (BH-161-q2): a device-id.json with a key given twice threw out of <see cref="InstallDeviceId.Stored"/>, and
	/// <see cref="InstallDeviceId.Current"/>'s fallback then skipped the window's slot: two windows shared one id. The key's last
	/// value is the id, the window takes its slot (here the second: a first window runs), and the file is repaired.
	/// </summary>
	[Fact]
	public void ADuplicateKey_StillGivesEachWindowItsSlot_AndTheFileIsRepaired()
	{
		var user = $@"{Environment.UserDomainName}\{Environment.UserName}";
		var worked = InstallDeviceId.Stored(_root, InstallDeviceId.PcId(), AppContext.BaseDirectory, user);
		var file = Path.Combine(_root, InstallDeviceId.FileName);
		var key = ((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!["ids"]!).Single().Key;
		var kept = Guid.NewGuid().ToString("N"); // the file's id, not the one worked out
		File.WriteAllText(file, $$$"""{"version": 1, "ids": {"{{{key}}}": "{{{worked}}}", "other": "{{{worked}}}", "{{{key}}}": "{{{kept}}}"}}""");
		using var first = InstallDeviceId.ClaimWindow(kept).Claim; // a first window of the copy runs

		InstallDeviceId.UseDataFolder(_root);
		var current = InstallDeviceId.Current;

		Assert.Equal(InstallDeviceId.ForWindow(kept, 1), current);
		var repaired = File.ReadAllText(file);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(repaired, key));
		var ids = System.Text.Json.Nodes.JsonNode.Parse(repaired)!["ids"]!.AsObject();
		Assert.Equal(kept, (string?)ids[key]);
		Assert.Equal(worked, (string?)ids["other"]); // every other entry kept
	}

	private string StoredId() => InstallDeviceId.Stored(_root, InstallDeviceId.PcId(), AppContext.BaseDirectory, $@"{Environment.UserDomainName}\{Environment.UserName}");

	/// <summary>
	/// 1.8.1 (the review of BH-175-m4): a window started while the copy's last window keeps the first slot only to send a film's
	/// last reports (another process: its slot and its closing mark) waits for that slot and is the copy's own device, not its
	/// second one for its whole run.
	/// </summary>
	[Fact]
	public async Task TheCurrentId_WhileTheLastWindowSendsItsLastReports_WaitsForItsSlot()
	{
		var id = StoredId();
		var held = new Mutex(false, $@"Local\Nixie-device-{id}-0");
		using var mark = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\Nixie-device-{id}-closing");
		var reported = Task.Run(async () =>
		{
			await Task.Delay(600); // its reports sent
			held.Dispose();
			mark.Set();
		});

		InstallDeviceId.UseDataFolder(_root);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var current = InstallDeviceId.Current;

		Assert.Equal(id, current); // the first slot: the copy's own device
		Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400), $"took it after {watch.ElapsedMilliseconds} ms");
		await reported;
	}

	/// <summary>
	/// 1.8.1 (the review of BH-175-m4): the wait is bounded (a closing window whose reports hang: the second slot after
	/// <see cref="InstallDeviceId.ClosingWait"/>, as before), and a window simply running marks nothing: a second window opened
	/// beside it is the second device at once. The mark goes with the slot.
	/// </summary>
	[Fact]
	public void AClosingWindowsSlot_IsWaitedFor_AtMostTheBound_AndARunningWindowsNotAtAll()
	{
		InstallDeviceId.UseDataFolder(_root);
		var current = InstallDeviceId.Current; // this window: the first slot
		var id = StoredId();
		Assert.Equal(id, current);

		var watch = System.Diagnostics.Stopwatch.StartNew();
		var (beside, besideClaim) = InstallDeviceId.ClaimWindow(id, TimeSpan.FromSeconds(10));
		besideClaim.Dispose();
		Assert.Equal(1, beside); // running, not closing: the second device
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"waited {watch.ElapsedMilliseconds} ms beside a running window");

		InstallDeviceId.Closing(); // its reports hang
		watch.Restart();
		var (late, lateClaim) = InstallDeviceId.ClaimWindow(id, TimeSpan.FromMilliseconds(500));
		lateClaim.Dispose();
		Assert.Equal(1, late);
		Assert.InRange(watch.Elapsed.TotalMilliseconds, 400, 5000);

		InstallDeviceId.ReleaseWindow(); // its reports' bound over: the slot and the mark go
		watch.Restart();
		var (next, nextClaim) = InstallDeviceId.ClaimWindow(id, TimeSpan.FromSeconds(10));
		nextClaim.Dispose();
		Assert.Equal(0, next);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"waited {watch.ElapsedMilliseconds} ms for a released slot");
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

	/// <summary>1.7.5 (BH-161-q2): a key given twice anywhere is read as JSON reads it, its last value, and written once.</summary>
	[Fact]
	public void ADuplicateKey_IsReadAsItsLastValue_AndWrittenOnce()
	{
		var program = Path.Combine(_root, "app");
		var id = InstallDeviceId.Stored(Data, Pc, program, User);
		var text = File.ReadAllText(IdFile);
		File.WriteAllText(IdFile, text.Replace("\"version\": 1", "\"version\": 1, \"version\": 1", StringComparison.Ordinal));
		Assert.ThrowsAny<Exception>(() => (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(IdFile)) as System.Text.Json.Nodes.JsonObject)?["ids"]); // what 1.6.1 met

		Assert.Equal(id, InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "elsewhere"), User));

		Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(IdFile), "\"version\""));
		Assert.Equal(id, InstallDeviceId.Stored(Data, Pc, Path.Combine(_root, "elsewhere"), User));
		Assert.Empty(Directory.GetFiles(Data, "*.tmp"));
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
