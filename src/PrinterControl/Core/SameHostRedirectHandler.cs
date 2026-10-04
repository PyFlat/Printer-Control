using System.Net;

namespace PrinterControl.Core;

// .NET's own redirects keep custom headers, so X-Api-Key would reach any host a server redirects to.
// This follows a redirect of a read only on the same host, never down to http; any other comes back as is.
internal sealed class SameHostRedirectHandler : DelegatingHandler
{
	private const int MaxRedirects = 5;

	public static HttpMessageHandler CreatePipeline() =>
		new SameHostRedirectHandler { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } };

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var response = await base.SendAsync(request, cancellationToken);
		for (var hop = 0; hop < MaxRedirects && Redirect(request, response) is { } next; hop++)
		{
			response.Dispose();
			request = next;
			response = await base.SendAsync(next, cancellationToken);
		}

		return response;
	}

	private static HttpRequestMessage? Redirect(HttpRequestMessage request, HttpResponseMessage response)
	{
		if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
				or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
			|| (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
			|| response.Headers.Location is not { } location
			|| request.RequestUri is not { } from
			|| !Uri.TryCreate(from, location, out var to)
			|| !string.Equals(to.Host, from.Host, StringComparison.OrdinalIgnoreCase)
			|| (to.Scheme != Uri.UriSchemeHttps && (to.Scheme != Uri.UriSchemeHttp || from.Scheme == Uri.UriSchemeHttps)))
		{
			return null;
		}

		var next = new HttpRequestMessage(request.Method, to);
		foreach (var header in request.Headers)
		{
			next.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}

		return next;
	}
}
