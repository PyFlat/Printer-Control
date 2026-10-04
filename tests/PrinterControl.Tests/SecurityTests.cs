using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using PrinterControl.Backends.OctoPrint;
using PrinterControl.Core;
using PrinterControl.Tests.Support;

namespace PrinterControl.Tests;

[TestFixture]
public sealed class SecurityTests
{
	[TestCase("../../system/commands/core/shutdown")]
	[TestCase("parts/../../system/commands/core/reboot")]
	[TestCase("./benchy.gcode")]
	public async Task A_file_path_cannot_reach_another_octoprint_endpoint(string path)
	{
		await using var server = await FakeOctoPrint.StartAsync();
		var api = new OctoPrintApi(new TestHttpClientFactory().CreateClient("test"), server.BaseUri, FakeOctoPrint.ApiKey);

		var failure = Assert.ThrowsAsync<PrinterException>(() => api.SelectFileAsync("local", path, print: true, CancellationToken.None));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(failure?.Failure, Is.EqualTo(PrinterFailure.BadRequest));
			Assert.That(server.Commands, Is.Empty, "nothing reached the server");
		}
	}

	private sealed class KeyRecorder : IAsyncDisposable
	{
		private WebApplication _app = null!;

		public Uri BaseUri { get; private set; } = null!;

		public List<string?> Keys { get; } = [];

		public static async Task<KeyRecorder> StartAsync(Func<HttpContext, IResult> answer)
		{
			var recorder = new KeyRecorder();
			(recorder._app, recorder.BaseUri) = await FakeServerHost.StartAsync(app => app.MapGet("/{**path}", (HttpContext http) =>
			{
				recorder.Keys.Add(http.Request.Headers["X-Api-Key"].FirstOrDefault());
				return answer(http);
			}));
			return recorder;
		}

		public async ValueTask DisposeAsync()
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}

	private static async Task<HttpResponseMessage> GetWithKeyAsync(Uri uri)
	{
		using var http = new TestHttpClientFactory().CreateClient("test");
		using var request = new HttpRequestMessage(HttpMethod.Get, uri);
		request.Headers.Add("X-Api-Key", "secret");
		return await http.SendAsync(request);
	}

	[Test]
	public async Task A_redirect_to_another_host_is_not_followed_so_the_key_stays_home()
	{
		await using var elsewhere = await KeyRecorder.StartAsync(_ => Results.Ok());
		// localhost and 127.0.0.1 are the same machine but different hosts, which is all the check sees.
		var target = new UriBuilder(elsewhere.BaseUri) { Host = "localhost" }.Uri;
		await using var printer = await KeyRecorder.StartAsync(_ => Results.Redirect(new Uri(target, "api/version").ToString()));

		using var response = await GetWithKeyAsync(new Uri(printer.BaseUri, "api/version"));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
			Assert.That(elsewhere.Keys, Is.Empty);
		}
	}

	[Test]
	public async Task A_redirect_on_the_same_host_is_followed_with_the_key()
	{
		await using var printer = await KeyRecorder.StartAsync(http => http.Request.Path == "/old"
			? Results.Redirect("/new")
			: Results.Ok());

		using var response = await GetWithKeyAsync(new Uri(printer.BaseUri, "old"));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
			Assert.That(printer.Keys, Is.EqualTo((string?[])["secret", "secret"]));
		}
	}
}
