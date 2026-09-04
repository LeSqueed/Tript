// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Tript.App.Tests;

// Serving a file that is still being written. The library lets a session be played while it records,
// so the content route has to survive a file that grows underneath it.
//
// The server used to declare Content-Length from a FileInfo taken before the file was opened, then
// copy the stream to EOF. The file grows in between, so the copy put more bytes on the wire than the
// response declared. HttpListener does not stop it: the surplus is written, and on a keep-alive
// connection a client that honours Content-Length reads the next response out of the middle of the
// last one.
//
// Asserted over a raw socket, because HttpClient stops at Content-Length and closes — it never sees
// the surplus that is the whole bug.
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

        // Big enough that the transfer takes long enough for the writer to overtake a length taken
        // before it started.
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

    // Sends one keep-alive GET and reads until the declared body has arrived, then keeps listening
    // briefly for anything the server writes past it — which is the whole point of the check.
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

        // Generous while the body is still owed; a second of quiet once it has all arrived is enough
        // to conclude the server wrote nothing more.
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

            // The failure this test exists for: more body than the response said there would be.
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
