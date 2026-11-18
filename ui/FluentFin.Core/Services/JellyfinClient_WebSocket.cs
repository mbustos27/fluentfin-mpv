﻿using System.Reactive.Disposables;
using System.Reactive.Linq;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

public partial class JellyfinClient
{
	private JellyfinWebSocketClient? _webSocket;

	public IObservable<T> ObserveWebSocket<T>() where T : class, IOutboundWebSocketMessage, new()
	{
		return Observable.Create<T>(observer => _webSocket?.Subscribe<T>(observer.OnNext) ?? Disposable.Empty);
	}

	private void StartWebSocketConnection()
	{
		// settings are recreated on every login, so the socket client is too
		_webSocket?.Dispose();
		_webSocket = new JellyfinWebSocketClient(_settings, new JellyfinWebSocketOptions
		{
			PeriodicMessageInterval = TimeSpan.FromSeconds(1.5),
			IdleTimeout = Timeout.InfiniteTimeSpan
		});
		_webSocket.ErrorOccurred += (_, e) => logger.LogError(e.Content, "WebSocket error");
		_webSocket.StateChanged += (_, e) => logger.LogInformation(e.Error, "WebSocket {State}", e.State);
		_webSocket.SubscribeAll(socketMessageSender.OnNext);
		_webSocket.Subscribe<Jellyfin.Sdk.Generated.Models.UserUpdatedMessage>(OnUserUpdated);
		_webSocket.Subscribe<Jellyfin.Sdk.Generated.Models.LibraryChangedMessage>(OnLibraryChanged);
	}

	// fluentfin-mpv: this user's library access or view order may have changed: read the views again (GetUserViews)
	internal void OnUserUpdated(Jellyfin.Sdk.Generated.Models.UserUpdatedMessage message)
	{
		if (message.Data?.Id == UserId)
		{
			ForgetUserViews();
		}
	}

	// fluentfin-mpv: a library may have been added, removed or renamed from another client: read the views again
	// (GetUserViews). Also sent for items added or changed in a library, so it costs at most one views request per use.
	internal void OnLibraryChanged(Jellyfin.Sdk.Generated.Models.LibraryChangedMessage message) => ForgetUserViews();

	/// <summary>Tests: the websocket closes (a client left behind kept reconnecting to the next test's fake server).</summary>
	internal void StopWebSocket()
	{
		_webSocket?.Dispose();
		_webSocket = null;
	}
}
