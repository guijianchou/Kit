// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class WebsiteTests
{
    [DataTestMethod]
    [DataRow(200, false, "", "PageResponded")]
    [DataRow(200, false, "<title>Just a moment...</title>", "BrowserCheck")]
    [DataRow(403, true, "", "BrowserCheck")]
    [DataRow(503, false, "", "ServerError")]
    public async Task InterruptedBodyKeepsHttpEvidenceAndPartialClassification(int status, bool challenge, string prefix, string outcome)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(new InterruptedBody(prefix)) };
        if (challenge)
        {
            response.Headers.Add("cf-mitigated", "challenge");
        }

        using var client = new HttpClient(new ResponseHandler(_ => response));
        var result = await NetworkProbe.CheckServiceAsync("Gemini", NetworkProbe.ServiceEndpoint("Gemini"), true, new(), CancellationToken.None, client);
        Assert.AreEqual(ProbeError.None, result.Error);
        Assert.AreEqual(status, result.Status);
        Assert.AreEqual(outcome, result.Outcome);
        Assert.IsTrue(result.BodyIncomplete);
        Assert.IsNotNull(result.ElapsedMs);
    }

    [TestMethod]
    public async Task HeadersTimingExcludesSlowBody()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new DelayedBody()) }));
        long start = Stopwatch.GetTimestamp();
        var result = await NetworkProbe.CheckServiceAsync("Gemini", NetworkProbe.ServiceEndpoint("Gemini"), true, new(), CancellationToken.None, client);
        double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Assert.AreEqual("PageResponded", result.Outcome);
        Assert.IsTrue(total - result.ElapsedMs >= 200, "Reading body must not inflate header response time.");
        Assert.IsFalse(result.BodyIncomplete);
    }

    [TestMethod]
    public async Task LoginRedirectRemainsReachable()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://accounts.google.com/signin"),
            Content = new StringContent("<html>Sign in</html>"),
        }));
        var result = await NetworkProbe.CheckServiceAsync("Gemini", new Uri("https://gemini.google.com/"), false, new(), CancellationToken.None, client);
        Assert.AreEqual(ProbeError.None, result.Error);
        Assert.AreEqual("SignIn", result.Outcome);
    }

    [TestMethod]
    public async Task FailureBeforeHeadersStillReportsConnectionError()
    {
        using var client = new HttpClient(new ResponseHandler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError)));
        var result = await NetworkProbe.CheckServiceAsync("Gemini", new Uri("https://gemini.google.com/"), false, new(), CancellationToken.None, client);
        Assert.AreEqual(ProbeError.Dns, result.Error);
        Assert.IsNull(result.Status);
    }

    [TestMethod]
    public async Task UserCancellationCannotBecomeReachableResult()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new ResponseHandler(_ =>
        {
            cancellation.Cancel();
            return new(HttpStatusCode.OK) { Content = new StringContent("hello") };
        }));
        await Assert.ThrowsAsync<OperationCanceledException>(() => NetworkProbe.CheckServiceAsync("Claude", new Uri("https://claude.ai/"), false, new(), cancellation.Token, client));
    }

    [DataTestMethod]
    [DataRow("Claude", "ip=198.51.100.8\nloc=JP\ncolo=NRT\n", "TraceResponded")]
    [DataRow("ChatGPT", "ip=198.51.100.8\nloc=JP\ncolo=NRT\n", "TraceResponded")]
    [DataRow("Claude", "<html>Welcome</html>", "TraceInvalid")]
    [DataRow("ChatGPT", "<html>Welcome</html>", "TraceInvalid")]
    [DataRow("Claude", "ip=invalid\nloc=JP", "TraceInvalid")]
    [DataRow("ChatGPT", "ip=invalid\nloc=JP", "TraceInvalid")]
    [DataRow("Claude", "<title>Just a moment...</title>", "BrowserCheck")]
    [DataRow("ChatGPT", "<title>Just a moment...</title>", "BrowserCheck")]
    public async Task ServiceTraceRequiresValidIdentityInsteadOfHttp200Alone(string name, string body, string outcome)
    {
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            Assert.AreEqual(name == "Claude" ? "https://api.anthropic.com/cdn-cgi/trace" : "https://chatgpt.com/cdn-cgi/trace", request.RequestUri!.AbsoluteUri);
            Assert.IsNull(request.Headers.Authorization);
            Assert.IsFalse(request.Headers.Contains("x-api-key"));
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var result = await NetworkProbe.CheckServiceAsync(name, NetworkProbe.ServiceEndpoint(name), true, new(), CancellationToken.None, client);
        Assert.AreEqual(ProbeError.None, result.Error);
        Assert.AreEqual(outcome, result.Outcome);
        if (outcome == "TraceResponded")
        {
            Assert.AreEqual("198.51.100.8", result.Checkpoint!.Ip);
            Assert.AreEqual("JP", result.Checkpoint.CountryCode);
        }
        else
        {
            Assert.IsNull(result.Checkpoint);
        }
    }

    [DataTestMethod]
    [DataRow("Claude")]
    [DataRow("ChatGPT")]
    public async Task PartialTraceCannotClaimCheckpointSuccess(string name)
    {
        using var client = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new InterruptedBody("ip=198.51.100.8\nloc=JP\n")),
        }));
        var result = await NetworkProbe.CheckServiceAsync(name, NetworkProbe.ServiceEndpoint(name), true, new(), CancellationToken.None, client);
        Assert.AreEqual("TraceInvalid", result.Outcome);
        Assert.IsTrue(result.BodyIncomplete);
        Assert.IsNull(result.Checkpoint);
    }

    [TestMethod]
    public async Task GoogleChecksTheWebsiteWithoutApiCredentials()
    {
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            Assert.AreEqual("https://www.google.com/", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.IsNull(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new StringContent("<html>Google</html>") };
        }));
        var result = await NetworkProbe.CheckServiceAsync("Google", NetworkProbe.ServiceEndpoint("Google"), true, new(), CancellationToken.None, client);
        Assert.AreEqual("PageResponded", result.Outcome);
        Assert.IsFalse(result.Failed);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class InterruptedBody(string prefix) : MemoryStream(Encoding.UTF8.GetBytes(prefix))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Position == Length ? ValueTask.FromException<int>(new IOException("Synthetic interrupted body")) : base.ReadAsync(buffer, cancellationToken);
    }

    private sealed class DelayedBody() : MemoryStream(Encoding.UTF8.GetBytes("<html>Ready</html>"))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0)
            {
                await Task.Delay(250, cancellationToken);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
