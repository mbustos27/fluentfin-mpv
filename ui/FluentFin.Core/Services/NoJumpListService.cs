using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Services;

/// <summary>
/// fluentfin-mpv: the jump list of a run with its own data folder (<see cref="KnownFolders.DataDirSwitch"/>): nothing. The
/// taskbar's jump list is shell state outside that folder, and a test run's libraries do not belong in it.
/// </summary>
public sealed class NoJumpListService : IJumpListService
{
	public Task Initialize(IEnumerable<BaseItemDto> libaries) => Task.CompletedTask;
}
