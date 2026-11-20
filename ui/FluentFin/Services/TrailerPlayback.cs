using FluentFin.Controls;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.1: what every Trailer button does (the shared item hero and each theme's): a local trailer plays in the mpv
/// player like any video, a YouTube one in the theatre window (<see cref="TrailerTheater"/>). A local trailer the server cannot
/// give falls back to the remote one. Nixie 1.4.0: the button never opens the browser by itself; a trailer on another site
/// (or one the theatre can't take) shows Nixie's message with the choice to open it (<see cref="Trailers.Plan"/>), and the
/// browser opens only from that choice (<see cref="TrailerBrowser"/>).
/// </summary>
public static class TrailerPlayback
{
	/// <summary>Whether a Trailer button shows for <paramref name="dto"/>.</summary>
	public static Visibility ButtonVisibility(BaseItemDto? dto) => Trailers.HasTrailer(dto) ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>Plays <paramref name="dto"/>'s trailer; <paramref name="button"/> gets the keyboard back when the theatre closes.</summary>
	public static Task Play(BaseItemDto? dto, Control? button)
	{
		ILogger? log = null;
		try { log = App.GetService<ILogger<TrailerTheater>>(); }
		catch (Exception) { }

		return PlayAsync(dto, new AppStage(button, log), log);
	}

	/// <summary>The Trailer button's steps, on <paramref name="stage"/> (the app's, or a test's).</summary>
	internal static async Task PlayAsync(BaseItemDto? dto, ITrailerStage stage, ILogger? log)
	{
		try
		{
			var choice = Trailers.Choose(dto);
			if (choice.Kind == TrailerKind.Local && dto?.Id is { } id)
			{
				BaseItemDto? local = null;
				try
				{
					local = await stage.LocalTrailer(id);
				}
				catch (Exception ex)
				{
					log?.LogWarning(ex, "[trailer] the server's local trailer could not be had");
				}

				if (local is not null)
				{
					log?.LogInformation("[trailer] local trailer {Id} plays in the player", local.Id);
					await stage.PlayLocal(local);
					return;
				}

				log?.LogInformation("[trailer] the server gave no local trailer; the remote one, if any");
				choice = Trailers.WithoutLocal(choice);
			}

			var step = Trailers.Plan(choice, stage.HasTheater);
			var title = ItemMeta.HeroTitle(dto);
			switch (step.Kind)
			{
				case TrailerStepKind.Theater:
					stage.OpenTheater(step.VideoId!, title);
					break;
				case TrailerStepKind.Message:
					stage.ShowMessage(step.Message!, title);
					break;
				case TrailerStepKind.Dialog:
					await stage.ShowDialog(step.Message!);
					break;
				default:
					log?.LogInformation("[trailer] no trailer to play");
					break;
			}
		}
		catch (Exception ex)
		{
			log?.LogWarning(ex, "[trailer] the trailer could not be played");
		}
	}

	/// <summary>Where the Trailer button's steps happen (the app's window; a test's stand-in). None of them opens the browser.</summary>
	internal interface ITrailerStage
	{
		/// <summary>Whether the theatre window is there.</summary>
		bool HasTheater { get; }

		/// <summary>The server's first local trailer of the item, null when it lists none.</summary>
		Task<BaseItemDto?> LocalTrailer(Guid itemId);

		/// <summary>Plays a local trailer in the player.</summary>
		Task PlayLocal(BaseItemDto trailer);

		/// <summary>Plays a YouTube trailer in the theatre.</summary>
		void OpenTheater(string videoId, string title);

		/// <summary>Shows Nixie's message in the theatre.</summary>
		void ShowMessage(TrailerMessage message, string title);

		/// <summary>No theatre: the same message and choice in a small dialog.</summary>
		Task ShowDialog(TrailerMessage message);
	}

	/// <summary>The app's stage: the window's theatre, the player, the server, a dialog.</summary>
	private sealed class AppStage(Control? button, ILogger? log) : ITrailerStage
	{
		public bool HasTheater => TrailerTheater.Current is not null;

		public async Task<BaseItemDto?> LocalTrailer(Guid itemId) =>
			(await App.GetService<IJellyfinClient>().GetLocalTrailers(itemId)).FirstOrDefault(t => t.Id is not null);

		public async Task PlayLocal(BaseItemDto trailer)
		{
			trailer.Type = BaseItemDto_Type.Trailer; // the queue plays a trailer alone (MpvQueue)
			await App.Commands.Play(trailer, null, streamsPicked: false, fromBeginning: true);
		}

		public void OpenTheater(string videoId, string title) =>
			TrailerTheater.Current?.Open(videoId, title, button, How);

		public void ShowMessage(TrailerMessage message, string title) =>
			TrailerTheater.Current?.OpenMessage(message, title, button, How);

		private FocusState How => button?.FocusState ?? FocusState.Programmatic;

		public async Task ShowDialog(TrailerMessage message)
		{
			log?.LogInformation("[trailer] no theatre: Nixie's message in a dialog: {Text}", message.AutomationText);
			if (button?.XamlRoot is not { } root)
			{
				return;
			}

			var dialog = new ContentDialog
			{
				Title = message.Heading,
				Content = new TextBlock { Text = message.Reason, TextWrapping = TextWrapping.Wrap },
				PrimaryButtonText = message.LinkText,
				CloseButtonText = "Close",
				DefaultButton = ContentDialogButton.Close,
				XamlRoot = root,
			};
			// the button system: the choice is Secondary (never the accent), Close is the default
			if (Application.Current.Resources.TryGetValue("NixieButtonStyle", out var secondary) && secondary is Style style)
			{
				dialog.PrimaryButtonStyle = style;
			}

			if (root.Content is FrameworkElement content)
			{
				dialog.RequestedTheme = content.ActualTheme;
			}

			try
			{
				if (await dialog.ShowAsync() == ContentDialogResult.Primary)
				{
					await TrailerBrowser.Open(message.Link, $"the user picked {message.LinkText}", log);
				}
			}
			catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
			{
				// another dialog is open: nothing (and no browser)
			}
		}
	}
}
