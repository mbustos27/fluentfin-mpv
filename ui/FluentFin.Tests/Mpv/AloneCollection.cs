namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv: tests that load libmpv or the WinUI and Windows SDK projections, spin threads or hold the thread pool
/// on purpose run alone, after the parallel ones: next to them, tests that time a child process to within a fraction of
/// a second (JobProcessTests) would measure their load instead.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AloneCollection
{
	public const string Name = "fluentfin-mpv: alone";
}
