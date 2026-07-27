// <copyright file="MoonrakerUploadOutcomeTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Backends;

public sealed class MoonrakerUploadOutcomeTests
{
    [Fact]
    public async Task UploadAndStartPrintAsync_ResponseLostAfterContentSent_ReturnsUnknown()
    {
        using var handler = new ResponseLostAfterContentHandler();
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());
        await using var content = new MemoryStream("G28\n"u8.ToArray());

        UploadAndPrintResult result =
            await ((ISupportsUploadAndPrint)client).UploadAndStartPrintAsync(
                "http://moonraker/",
                "pf-attempt.gcode",
                content,
                ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(UploadAndPrintOutcome.Unknown);
        handler.ContentWasRead.Should().BeTrue(
            "the transport failed only after the start-capable request body was sent");
    }

    private sealed class ResponseLostAfterContentHandler : HttpMessageHandler
    {
        public bool ContentWasRead { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            ContentWasRead = true;
            throw new HttpRequestException("Response lost after send.");
        }
    }
}
