﻿using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Microsoft.Extensions.Logging;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Hosting;
using ReactiveUI;

namespace FluentFin.Services;

public class WebSocketMessageHandler(IObservable<IOutboundWebSocketMessage> webSocketMessages,
									 IContentDialogService contentDialogService,
									 INavigationService navigationService,
									 ISettings settings,
									 ILogger<WebSocketMessageHandler> logger) : IHostedService
{
	private readonly CompositeDisposable _disposables = [];

	public Task StartAsync(CancellationToken cancellationToken)
	{
		webSocketMessages
		 .ObserveOn(RxSchedulers.MainThreadScheduler)
		 .Subscribe(message =>
		 {
			 switch (message)
			 {
				 case GeneralCommandMessage { Data.Name: GeneralCommand_Name.DisplayMessage } gcm:
					 contentDialogService.Growl((string)gcm.Data.Arguments!.AdditionalData["Header"],
												(string)gcm.Data.Arguments!.AdditionalData["Text"],
												TimeSpan.FromMilliseconds(double.Parse((string)gcm.Data.Arguments.AdditionalData["TimeoutMs"])));
					 break;
				 case SyncPlayGroupUpdateMessage { Data: { } update }:
					 HandleGroupUpdate(update);
					 break;
				 // casting to FluentFin: with the mpv player the backend plays the queue the controller sent
				 case PlayMessage { Data: { } play } when settings.MediaPlayer == MediaPlayerType.Mpv:
					 HandleRemotePlay(play);
					 break;
			 }
		 })
		 .DisposeWith(_disposables);

		return Task.CompletedTask;
	}

	private void HandleRemotePlay(PlayRequest play)
	{
		if (MpvRemoteControl.ToPlayRequest(play) is not { } request)
		{
			logger.LogInformation("Remote {Command} with {Count} item(s) not taken: the MPV player can only replace what plays", play.PlayCommand, play.ItemIds?.Count ?? 0);
			contentDialogService.Growl("", "Adding to the queue is not available with the MPV player.", TimeSpan.FromSeconds(5));
			return;
		}

		logger.LogInformation("Remote {Command}: {Count} item(s) from index {Index}", play.PlayCommand, request.ItemIds.Count, request.StartIndex);
		navigationService.NavigateTo<VideoPlayerViewModel>(request);
	}

	private void HandleGroupUpdate(GroupUpdate update)
	{
		switch (update)
		{
			case { SyncPlayPlayQueueUpdate.Data: { PlayingItemIndex: >= 0 } playQueue }:
				navigationService.NavigateTo<VideoPlayerViewModel>(playQueue);
				break;
			case { SyncPlayGroupJoinedUpdate: { } joined }:
				contentDialogService.Growl("", $"SyncPlay Enabled", TimeSpan.FromSeconds(5));
				SessionInfo.GroupId = joined.Data?.GroupId;
				break;
			case { SyncPlayGroupLeftUpdate: not null }:
				contentDialogService.Growl("", $"SyncPlay Disabled", TimeSpan.FromSeconds(5));
				SessionInfo.GroupId = null;
				break;
			case { SyncPlayUserJoinedUpdate: { } userJoined }:
				contentDialogService.Growl("", $"{userJoined.Data} joined", TimeSpan.FromSeconds(5));
				break;
			case { SyncPlayUserLeftUpdate: { } userLeft }:
				contentDialogService.Growl("", $"{userLeft.Data} left", TimeSpan.FromSeconds(5));
				break;
		}
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		if (_disposables.IsDisposed)
		{
			return Task.CompletedTask;
		}

		_disposables.Dispose();

		return Task.CompletedTask;
	}
}
