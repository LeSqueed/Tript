// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Tript.App.Tests;

[Collection(AppHostCollection.Name)]
public sealed class GrowingFileStreamingTests
{
    private readonly string _contentRoot;
    private readonly string _settingsPath;

    public GrowingFileStreamingTests(AppHostCollectionFixture fixture)
    {
        _contentRoot = fixture.NewContentRoot(nameof(GrowingFileStreamingTests));
        _settingsPath = fixture.NewSettingsPath(nameof(GrowingFileStreamingTests));
    }

    [Fact]
    public async Task AFileStillBeingWritten_PutsNoMoreBytesOnTheWireThanItDeclares()
    {
        var file = Path.Combine(_contentRoot, "sessions", "live.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var chunk = new byte[64 * 1024];
        Array.Fill(chunk, (byte)'x');

        await using (var seed = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            for (var index = 0; index < 128; index++)
                await seed.WriteAsync(chunk);
        }

        var host = AppHostDriver.StartFake(_contentRoot, _settingsPath);
        await using var _ = host;

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            await using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            for (var index = 0; index < 256 && !stop.IsCancellationRequested; index++)
            {
                await stream.WriteAsync(chunk, CancellationToken.None);
                await stream.FlushAsync(CancellationToken.None);
            }
        });

        try
        {
            var (declared, received) = await ReadWholeResponseAsync(host, "/api/content/sessions/live.mp4");

            Assert.True(declared > 0, "the response must declare a length");
            Assert.Equal(declared, received);
        }
        finally
        {
            await stop.CancelAsync();
            await writer;
        }

        await host.ShutdownAsync();
    }

    private static async Task<(long Declared, long Received)> ReadWholeResponseAsync(AppHostDriver host, string path)
    {
        path = host.WithToken(path);
        using var client = new TcpClient();
        await client.ConnectAsync("localhost", host.ContentPort);
        await using var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: localhost:{host.ContentPort}\r\n\r\n"));

        using var response = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long declared = -1;
        var headerEnd = -1;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var body = headerEnd >= 0 ? response.Length - (headerEnd + 4) : 0;
            var complete = declared >= 0 && body >= declared;

            using var idle = new CancellationTokenSource(complete ? 1000 : 20000);
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, idle.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException
                                                 or SocketException)
            {
                break;
            }

            if (read == 0)
                break;
            response.Write(buffer, 0, read);

            if (headerEnd < 0)
            {
                var bytes = response.ToArray();
                headerEnd = FindHeaderEnd(bytes);
                if (headerEnd >= 0)
                    declared = DeclaredLength(Encoding.ASCII.GetString(bytes, 0, headerEnd));
            }

            if (declared >= 0 && response.Length - (headerEnd + 4) > declared)
                break;
        }

        Assert.True(headerEnd > 0, "the response had no header/body separator");
        return (declared, response.Length - (headerEnd + 4));
    }

    private static long DeclaredLength(string headers) => headers.Split("\r\n")
        .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
        .Select(line => long.Parse(line.Split(':')[1].Trim()))
        .FirstOrDefault(-1);

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (var index = 0; index + 3 < bytes.Length; index++)
        {
            if (bytes[index] == '\r' && bytes[index + 1] == '\n'
                && bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                return index;
        }

        return -1;
    }
}
