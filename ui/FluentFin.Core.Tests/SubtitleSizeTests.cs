using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;

namespace FluentFin.Core.Tests;

/// <summary>fluentfin-mpv: the subtitles menu's Text size (50 to 200 %, steps of 10, mpv's sub-scale).</summary>
public sealed class SubtitleSizeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));
	private readonly List<(string Command, JsonObject? Args)> _sent = [];

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}

	private MpvControlsViewModel NewVm() => new((command, args) =>
	{
		_sent.Add((command, args?.DeepClone() as JsonObject));
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData(100, 1.0)]
	[InlineData(50, 0.5)]
	[InlineData(120, 1.2)]
	[InlineData(200, 2.0)]
	[InlineData(10, 0.5)]
	[InlineData(500, 2.0)]
	[InlineData(133, 1.3)]
	public void Percent_maps_to_sub_scale_within_range_and_on_a_step(int percent, double scale) => Assert.Equal(scale, SubtitleSizes.ToScale(percent), 3);

	[Fact]
	public void The_text_shows_the_percent() => Assert.Equal("120%", SubtitleSizes.Text(120));

	[Fact]
	public async Task A_new_size_is_remembered_and_sent_once()
	{
		var vm = NewVm();
		var remembered = new List<int>();
		vm.SubtitleSizeChanged = remembered.Add;

		await vm.SetSubtitleSize(120);
		await vm.SetSubtitleSize(120);

		Assert.Equal([120], remembered);
		Assert.Equal("120%", vm.SubtitleSizeText);
		var sent = Assert.Single(_sent);
		Assert.Equal(Bridge.Commands.SetSubtitleSize, sent.Command);
		Assert.Equal(120, (int)sent.Args!["percent"]!);
	}

	[Fact]
	public async Task Reset_to_100_goes_back_and_is_sent()
	{
		var vm = NewVm();
		await vm.SetSubtitleSize(160);
		await vm.SetSubtitleSize(SubtitleSizes.Default);

		Assert.Equal(100, (int)_sent[^1].Args!["percent"]!);
	}

	[Fact]
	public void The_remembered_size_is_sent_when_the_controls_take_over()
	{
		var vm = NewVm();
		vm.SubtitleSize = 140;

		vm.SendControls();

		Assert.Contains(_sent, x => x.Command == Bridge.Commands.SetSubtitleSize && (int)x.Args!["percent"]! == 140);
	}

	[Fact]
	public void The_size_is_kept_in_the_local_settings_across_restarts()
	{
		var first = new Settings.Settings(new LocalSettingsService(new KnownFolders(_root)));
		Assert.Equal(100, first.SubtitleSize);
		first.SubtitleSize = 130;

		var restarted = new Settings.Settings(new LocalSettingsService(new KnownFolders(_root)));
		Assert.Equal(130, restarted.SubtitleSize);
		restarted.SubtitleSize = 999;
		Assert.Equal(200, restarted.SubtitleSize);
	}
}
