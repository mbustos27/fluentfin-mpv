namespace FluentFin.Core.Outside;

/// <summary>1.7: one request as it left Nixie: method, address, every header (the request's and its content's) and the body.</summary>
public sealed record OutsideRequestRecord(string Method, Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body)
{
	/// <summary>Everything the request carried, as one text: what the privacy test searches for a library's ids and titles.</summary>
	public string AllText =>
		$"{Method} {Url.OriginalString}\n{string.Join("\n", Headers.Select(h => h.Key + ": " + h.Value))}\n\n{Body}";
}

/// <summary>
/// 1.7: records every outgoing request (address, headers, body) before handing it on: the privacy test runs every enabled
/// source through it against the strict fakes and asserts that nothing from the library left. Tests and the live check use
/// it; the app doesn't.
/// </summary>
public sealed class OutsideRequestRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
{
	private readonly List<OutsideRequestRecord> _requests = [];
	private readonly Lock _lock = new();

	/// <summary>Every request so far, in the order sent.</summary>
	public IReadOnlyList<OutsideRequestRecord> Requests
	{
		get
		{
			lock (_lock)
			{
				return [.. _requests];
			}
		}
	}

	/// <summary>When set, a request whose host it refuses is not sent: the handler answers with an exception instead (the live check: only its named hosts).</summary>
	public Func<Uri, bool>? AllowHost { get; init; }

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var headers = request.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))).ToList();
		var body = "";
		if (request.Content is { } content)
		{
			headers.AddRange(content.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))));
			body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		}

		lock (_lock)
		{
			_requests.Add(new OutsideRequestRecord(request.Method.Method, request.RequestUri!, headers, body));
		}

		if (AllowHost is { } allow && !allow(request.RequestUri!))
		{
			throw new HttpRequestException($"Not an allowed host: {request.RequestUri!.Host}");
		}

		return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
	}
}
