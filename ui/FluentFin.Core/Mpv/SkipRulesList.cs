using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluentFin.Core.Mpv;

/// <summary>1.6 skip: one line of Settings' "Skip rules" list: a show, or one of its rules or learned intros.</summary>
public sealed record SkipRuleRow(SkipRuleRowKind Kind, string SeriesId, string? RuleId, string Title, string Detail, string ForgetName)
{
	public bool IsShow => Kind == SkipRuleRowKind.Show;
}

public enum SkipRuleRowKind
{
	Show,
	Rule,
	LearnedIntro,
}

/// <summary>
/// 1.6 skip: Settings > Playback > "Skip rules" (replaces 1.4's "Forget learned intros"): each show with its rules and learned
/// intros, a Forget for each and for each show, and Forget all. The backend keeps them (<c>skip-rules.json</c>, its only
/// writer), so this asks it (docs/BRIDGE.md §4 <c>skip_rules</c>, <c>forget_skip_rules</c>): the backend is started if it
/// is not running (no mpv, no sign-in of its own: the account is named by the server's and the user's ids, no token).
/// </summary>
public sealed partial class SkipRulesList(IMpvBackend backend, Func<(string? ServerId, string? UserId)> account) : ObservableObject
{
	/// <summary>How long one question to the backend may take (a cold start of the backend included).</summary>
	public static readonly TimeSpan AskFor = TimeSpan.FromSeconds(20);

	public ObservableCollection<SkipRuleRow> Rows { get; } = [];

	/// <summary>What the list says above its rows: loading, empty, or what went wrong; "" when rows show.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasStatus))]
	public partial string Status { get; set; } = "";

	public bool HasStatus => Status.Length > 0;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanForgetAll))]
	public partial bool HasRows { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanForgetAll))]
	public partial bool IsBusy { get; set; }

	public bool CanForgetAll => HasRows && !IsBusy;

	public const string Empty = "No skip rules yet. In the player, Shift+S marks a part to skip in every episode of a show.";
	public const string NoAccount = "Sign in to see your skip rules.";
	public const string Unreachable = "Couldn't reach the mpv player to read the skip rules. Try again in a moment.";

	/// <summary>Reads the list from the backend. Never throws.</summary>
	public async Task LoadAsync()
	{
		var (serverId, userId) = account();
		if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(userId))
		{
			Show([], NoAccount);
			return;
		}

		IsBusy = true;
		Status = "Loading…";
		try
		{
			await backend.StartAsync();
			var result = await backend.SendAsync(Bridge.Commands.SkipRules, BridgeSkipCommands.Account(serverId, userId), AskFor);
			var list = result?.Deserialize<BridgeSkipRulesList>(Bridge.JsonOptions);
			var rows = RowsOf(list);
			Show(rows, rows.Count == 0 ? Empty : "");
		}
		catch (Exception)
		{
			Show([], Unreachable);
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>Forgets one row (a rule, a learned intro, or a show with everything of it), then reads the list again.</summary>
	public Task ForgetAsync(SkipRuleRow row) => row.IsShow
		? ForgetCoreAsync(row.SeriesId, null)
		: ForgetCoreAsync(null, row.RuleId);

	/// <summary>Forgets everything of this account (Settings asks first).</summary>
	public Task ForgetAllAsync() => ForgetCoreAsync(null, null);

	private async Task ForgetCoreAsync(string? seriesId, string? ruleId)
	{
		var (serverId, userId) = account();
		if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(userId))
		{
			Show([], NoAccount);
			return;
		}

		IsBusy = true;
		try
		{
			await backend.StartAsync();
			await backend.SendAsync(Bridge.Commands.ForgetSkipRules, BridgeSkipCommands.Forget(seriesId, ruleId, serverId, userId), AskFor);
		}
		catch (Exception)
		{
			IsBusy = false;
			Status = "Couldn't forget that. Try again in a moment.";
			return;
		}

		IsBusy = false;
		await LoadAsync();
	}

	private void Show(IReadOnlyList<SkipRuleRow> rows, string status)
	{
		Rows.Clear();
		foreach (var row in rows)
		{
			Rows.Add(row);
		}

		HasRows = rows.Count > 0;
		Status = status;
	}

	/// <summary>The rows for a <c>skip_rules</c> result: each show, then its rules, then its learned intros.</summary>
	public static List<SkipRuleRow> RowsOf(BridgeSkipRulesList? list)
	{
		var rows = new List<SkipRuleRow>();
		foreach (var show in list?.Shows ?? [])
		{
			var name = string.IsNullOrWhiteSpace(show.Name) ? "A show" : show.Name.Trim();
			var rules = show.Rules ?? [];
			var intros = show.LearnedIntros ?? [];
			rows.Add(new SkipRuleRow(SkipRuleRowKind.Show, show.SeriesId, null, name, ShowDetail(rules.Count, intros, show.Refused),
				$"Forget everything for {name}"));
			foreach (var rule in rules)
			{
				var where = SkipRuleText.Where(rule.Anchor, rule.StartS, rule.EndS);
				rows.Add(new SkipRuleRow(SkipRuleRowKind.Rule, show.SeriesId, rule.Id, where,
					$"{SkipRuleText.What(rule.Action)} · {SkipRuleText.Origin(rule.Origin)}", $"Forget {where} of {name}"));
			}

			foreach (var intro in intros)
			{
				var title = intro.Season is int season ? $"Season {season} intro" : "Intro";
				var detail = intro.Moves
					? "Moves between episodes, so it's not offered"
					: $"{SkipRuleText.Time(intro.StartS)} to {SkipRuleText.Time(intro.EndS)} · Learned from your skips";
				rows.Add(new SkipRuleRow(SkipRuleRowKind.LearnedIntro, show.SeriesId, intro.Id, title, detail, $"Forget the {title.ToLowerInvariant()} of {name}"));
			}
		}

		return rows;
	}

	private static string ShowDetail(int rules, IReadOnlyList<BridgeLearnedIntro> intros, bool refused)
	{
		var parts = new List<string>();
		if (rules > 0)
		{
			parts.Add(rules == 1 ? "1 rule" : $"{rules} rules");
		}

		if (intros.Count > 0)
		{
			parts.Add(intros.Count == 1 ? "1 learned intro" : $"{intros.Count} learned intros");
		}

		if (refused)
		{
			parts.Add("No suggestions (you said never)");
		}

		return parts.Count == 0 ? "Nothing kept" : string.Join(" · ", parts);
	}
}
