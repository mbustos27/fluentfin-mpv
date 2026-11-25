namespace FluentFin.Tests;

/// <summary>
/// 1.7.5 (the review's Minor): the close's watch (g4's WatchWindowClose, and WatchExit) runs before MainWindow.Close() in
/// OnClosing's finally; a throw there (the shell's frame read while the window goes) left the window open and the process
/// hidden. The page's name is read inside a try, and the window closes whatever the watch did.
/// </summary>
public sealed class CloseWatchSafetyTests
{
	private static string App() => File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "App.xaml.cs")));

	private static string Body(string code, string signature)
	{
		var start = code.IndexOf(signature, StringComparison.Ordinal);
		Assert.True(start >= 0, signature);
		var open = code.IndexOf('{', start);
		var depth = 0;
		for (var i = open; i < code.Length; i++)
		{
			depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
			if (depth == 0)
			{
				return code[open..(i + 1)];
			}
		}

		throw new InvalidOperationException("unbalanced braces");
	}

	[Fact]
	public void ThePagesName_IsReadInsideATry()
	{
		var body = Body(App(), "private static Services.CloseWatch WatchWindowClose(");
		var read = body.IndexOf("GetService<INavigationService>()", StringComparison.Ordinal);
		var firstTry = body.IndexOf("try", StringComparison.Ordinal);

		Assert.True(read > 0 && firstTry > 0 && firstTry < read, "the frame is read before any try in WatchWindowClose");
	}

	[Fact]
	public void TheWindowCloses_WhateverTheWatchDid()
	{
		var code = App();
		var close = code.IndexOf("MainWindow.Close();", StringComparison.Ordinal);
		var watch = code.LastIndexOf("watch = WatchWindowClose(log);", close, StringComparison.Ordinal);
		Assert.True(watch > 0, "the watch is set up before the close");
		var between = code[watch..close];
		// the watch's set-up is caught (a throw there doesn't skip the close), and the close is no part of a using whose
		// set-up could throw before it
		Assert.Contains("catch (Exception ex)", between, StringComparison.Ordinal);
		Assert.DoesNotContain("using (var watch = WatchWindowClose", code, StringComparison.Ordinal);
	}
}
