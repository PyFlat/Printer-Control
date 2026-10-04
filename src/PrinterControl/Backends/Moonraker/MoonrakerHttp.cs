using System.Net;
using System.Text.Json;
using PrinterControl.Core;

namespace PrinterControl.Backends.Moonraker;

// The few plain HTTP calls of detection and setup; the connection itself uses the websocket.
internal sealed class MoonrakerHttp(HttpClient http, Uri baseUri)
{
	public sealed record Answer(HttpStatusCode Status, JsonElement? Json)
	{
		public JsonElement? Result => Json?.TryGetProperty("result", out var result) == true ? result : null;
	}

	public async Task<Answer> GetAsync(string path, string? apiKey, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(PrinterUrls.Normalize(baseUri), path));
		if (!string.IsNullOrEmpty(apiKey))
		{
			request.Headers.Add("X-Api-Key", apiKey);
		}

		HttpResponseMessage response;
		try
		{
			response = await http.SendAsync(request, cancellationToken);
		}
		catch (HttpRequestException exception)
		{
			throw new PrinterException(PrinterFailure.Unreachable, $"Could not reach {baseUri}: {exception.Message}", exception);
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			throw new PrinterException(PrinterFailure.Unreachable, $"{baseUri} did not answer in time.", exception);
		}

		using (response)
		{
			if (response.Content.Headers.ContentType?.MediaType != "application/json")
			{
				return new Answer(response.StatusCode, null);
			}

			try
			{
				using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
				return new Answer(response.StatusCode, json.RootElement.Clone());
			}
			catch (JsonException)
			{
				return new Answer(response.StatusCode, null);
			}
		}
	}

	// Moonraker answers /server/info with its version, or, without access, with its own error object
	// ({"error": {"code": 401, "message": ...}}). OctoPrint and plain web servers answer neither.
	public static bool IsMoonraker(Answer info) => info.Status switch
	{
		HttpStatusCode.OK => info.Result?.Str("moonraker_version") is not null,
		HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => info.Json?.Obj("error")?.Num("code") is not null,
		_ => false,
	};
}
