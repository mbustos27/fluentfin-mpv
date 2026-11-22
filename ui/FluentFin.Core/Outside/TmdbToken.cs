using System.Security.Cryptography;
using System.Text.Json;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7 trending: the user's own TMDb API Read Access Token for "TMDb trending" (each user brings their own: the roadmap's
/// "Decided for 1.6 and 1.7"). Kept on this PC in the settings file, encrypted with Windows DPAPI for the current Windows
/// user (with the settings' own entropy, as saved passwords are), so another Windows account or PC can't read it (DPAPI is
/// Windows': elsewhere nothing is kept).
/// <para>
/// It is never logged and never put in an address: the only way out is <see cref="TmdbLists.Headers"/>, as
/// <c>Authorization: Bearer</c>. <see cref="ToString"/> says whether one is kept, never what it is.
/// </para>
/// </summary>
public sealed class TmdbToken(ILocalSettingsService settings)
{
	/// <summary>The settings entry: the token's DPAPI-protected bytes, or null when there is none (<see cref="Settings.SettingKeys.TmdbReadAccessToken"/>).</summary>
	public static string SettingName => Settings.SettingKeys.TmdbReadAccessToken.Name;

	/// <summary>Raised when the token is saved or cleared (Settings shows "Saved", the source reads it again).</summary>
	public event EventHandler? Changed;

	/// <summary>Whether a token is kept that this Windows user can read.</summary>
	public bool HasToken => Read() is not null;

	/// <summary>The token, or null: none kept, or one this Windows user can't decrypt (another account's, another PC's).</summary>
	public string? Read()
	{
		if (!OperatingSystem.IsWindows() || Protected() is not { Length: > 0 } bytes)
		{
			return null;
		}

		try
		{
			var token = bytes.Unprotect(settings.GetEntropyBytes()).Trim();
			return token.Length == 0 ? null : token;
		}
		catch (CryptographicException)
		{
			return null;
		}
	}

	/// <summary>
	/// Keeps <paramref name="token"/> (trimmed) in place of the one kept; a blank one clears it. Returns what's wrong with it
	/// in plain words (<see cref="TmdbLists.TokenProblem"/>) and keeps nothing then.
	/// </summary>
	public string? Save(string? token)
	{
		var t = token?.Trim() ?? "";
		if (t.Length == 0)
		{
			Clear();
			return null;
		}

		if (TmdbLists.TokenProblem(t) is { } problem)
		{
			return problem;
		}

		if (!OperatingSystem.IsWindows())
		{
			return "Keeping a token needs Windows.";
		}

		settings.SaveSetting(SettingName, t.Protect(settings.GetEntropyBytes()));
		Changed?.Invoke(this, EventArgs.Empty);
		return null;
	}

	/// <summary>Forgets the token.</summary>
	public void Clear()
	{
		if (settings.ReadSettingRaw(SettingName) is null)
		{
			return;
		}

		settings.SaveSetting<byte[]?>(SettingName, null);
		Changed?.Invoke(this, EventArgs.Empty);
	}

	private byte[]? Protected()
	{
		try
		{
			return settings.ReadSettingRaw(SettingName)?.Deserialize<byte[]>();
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
			return null;
		}
	}

	/// <summary>Never the token: "TMDb token: kept" or "TMDb token: none".</summary>
	public override string ToString() => HasToken ? "TMDb token: kept" : "TMDb token: none";
}
