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
		StopWebSocket();
		var socket = new JellyfinWebSocketClient(_settings, new JellyfinWebSocketOptions
		{
			PeriodicMessageInterval = TimeSpan.FromSeconds(1.5),
			IdleTimeout = Timeout.InfiniteTimeSpan
		});
		_webSocket = socket;
		socket.ErrorOccurred += (_, e) => logger.LogError(e.Content, "WebSocket error");
		socket.StateChanged += (_, e) => logger.LogInformation(e.Error, "WebSocket {State}", e.State);
		socket.SubscribeAll(socketMessageSender.OnNext);
		socket.Subscribe<Jellyfin.Sdk.Generated.Models.UserUpdatedMessage>(OnUserUpdated);
		socket.Subscribe<Jellyfin.Sdk.Generated.Models.LibraryChangedMessage>(OnLibraryChanged);
		// 1.6.1 (the 1.5.1 review): the socket dials again with the token it has, and the server refuses it every time once the
		// sign-in ended: it stops then, and the next sign-in starts a new one (Initialize)
		_stopWhenSignInEnds = () =>
		{
			if (SignInState.Ended && StopWebSocket(socket))
			{
				logger.LogInformation("[sign-in] the sign-in ended: the websocket stops until the next sign-in");
			}
		};
		SignInState.Changed += _stopWhenSignInEnds;
	}

	private Action? _stopWhenSignInEnds;

	/// <summary>1.6.1 tests: whether a websocket is open or dialling.</summary>
	internal bool WebSocketRunning => _webSocket is not null;

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
		if (_webSocket is { } socket)
		{
			StopWebSocket(socket);
		}
	}

	/// <summary>1.6.1: closes <paramref name="socket"/> if it is still this client's; false when another took its place.</summary>
	private bool StopWebSocket(JellyfinWebSocketClient socket)
	{
		if (!ReferenceEquals(Interlocked.CompareExchange(ref _webSocket, null, socket), socket))
		{
			return false;
		}

		if (Interlocked.Exchange(ref _stopWhenSignInEnds, null) is { } watch)
		{
			SignInState.Changed -= watch;
		}

		socket.Dispose();
		return true;
	}
}
