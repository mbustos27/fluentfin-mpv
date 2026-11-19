using FluentFin.Controls;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.1: what every Trailer button does (the shared item hero and each theme's): a local trailer plays in the mpv
/// player like any video, a YouTube one in the theatre window (<see cref="TrailerTheater"/>), any other link in the browser
/// (<see cref="Trailers.Choose"/>). A local trailer the server cannot give falls back to the remote one.
/// </summary>
public static class TrailerPlayback
{
	/// <summary>Whether a Trailer button shows for <paramref name="dto"/>.</summary>
	public static Visibility ButtonVisibility(BaseItemDto? dto) => Trailers.HasTrailer(dto) ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>Plays <paramref name="dto"/>'s trailer; <paramref name="button"/> gets the keyboard back when the theatre closes.</summary>
	public static async Task Play(BaseItemDto? dto, Control? button)
	{
		var choice = Trailers.Choose(dto);
		var log = App.GetService<ILogger<TrailerTheater>>();
		try
		{
			if (choice.Kind == TrailerKind.Local && dto?.Id is { } id)
			{
				var local = (await App.GetService<IJellyfinClient>().GetLocalTrailers(id)).FirstOrDefault(t => t.Id is not null);
				if (local is not null)
				{
					log.LogInformation("[trailer] local trailer {Id} plays in the player", local.Id);
					local.Type = BaseItemDto_Type.Trailer; // the queue plays a trailer alone (MpvQueue)
					await App.Commands.Play(local, null, streamsPicked: false, fromBeginning: true);
					return;
				}

				log.LogInformation("[trailer] the server listed no local trailer; the remote one, if any");
				choice = Trailers.WithoutLocal(choice);
			}

			switch (choice.Kind)
			{
				case TrailerKind.YouTube when TrailerTheater.Current is { } theater:
					theater.Open(choice.YouTubeId!, ItemMeta.HeroTitle(dto), button, button?.FocusState ?? FocusState.Programmatic);
					break;
				case TrailerKind.YouTube or TrailerKind.Browser:
					await Launcher.LaunchUriAsync(choice.Url!);
					break;
			}
		}
		catch (Exception ex)
		{
			log.LogWarning(ex, "[trailer] the trailer could not be played");
		}
	}
}
