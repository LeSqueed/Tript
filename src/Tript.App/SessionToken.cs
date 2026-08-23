// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Tript.App;

// The per-launch session token, required on all three loopback listeners.
//
// The control socket's Origin check stops a malicious web page and nothing else: loopback is NOT
// user-scoped, so another user's process on this machine can connect, and a non-browser client
// sends no Origin at all — which the allowlist has to accept, or the app's own webview is locked
// out. The content server checks no Origin by design (a <video> element sends none), so a page that
// guesses a path could embed and play a recording. This closes both.
//
// It is not a defence against a process running as the SAME user: that process can read the
// recordings off disk anyway.
//
// Generated once per launch, held in memory, never persisted, never logged, and never put on a
// command line — /proc/<pid>/cmdline is world-readable on Linux, so a token there is worse than no
// token at all, because it looks safe.
internal sealed class SessionToken
{
    // The query parameter every listener reads it from. A browser cannot set a header on a
    // WebSocket handshake, and a <video src> can carry nothing but a URL, so the query string is the
    // only channel all three share.
    internal const string QueryKey = "k";

    // The cookie the UI host hands back, so the assets the document pulls in do not each need the
    // token spelled out in their URL. The UI host also accepts a same-origin referrer fallback for
    // embedded profiles that do not replay this cookie on subresource requests.
    internal const string CookieName = "tript_session";

    private const int TokenBytes = 32;

    // The token as UTF-8, compared byte-for-byte in fixed time.
    private readonly byte[] _expected;

    internal SessionToken()
    {
        Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
        _expected = Encoding.UTF8.GetBytes(Value);
    }

    // The token itself. Only two places may see it: the URL printed on stdout for the user's own
    // terminal, and the URL the desktop shell loads in-process.
    internal string Value { get; }

    // The URL the shell opens and the headless user pastes into a browser.
    internal string UiUrl => BuildUiUrl(LocalPorts.Ui);

    internal string BuildUiUrl(int port) => $"http://localhost:{port}/?{QueryKey}={Value}";

    internal bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;

        // FixedTimeEquals over the raw bytes. It answers false for a length mismatch without
        // looking at the contents, which is right: the length is not the secret.
        return CryptographicOperations.FixedTimeEquals(_expected, Encoding.UTF8.GetBytes(presented));
    }

    // Whether a request carries the token, in the query string or (UI host only) in the cookie the
    // document request was given.
    internal bool Authorises(HttpListenerRequest request, bool acceptCookie = false)
    {
        if (Matches(FromQuery(request)))
            return true;

        return acceptCookie && Matches(FromCookie(request));
    }

    // Some embedded WebView2 profiles do not replay a Set-Cookie header for subresource requests.
    // Same-origin requests still carry the launch URL as their referrer, so the UI host can recover
    // the token without weakening the other listeners or accepting a token from another origin.
    internal bool AuthorisesUi(HttpListenerRequest request)
    {
        if (Authorises(request, acceptCookie: true))
            return true;

        var referrer = request.UrlReferrer;
        var requestUrl = request.Url;
        if (referrer is null || requestUrl is null ||
            Uri.Compare(referrer, requestUrl, UriComponents.SchemeAndServer,
                UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        return Matches(FromQuery(referrer));
    }

    private static string? FromQuery(HttpListenerRequest request)
    {
        return FromQuery(request.Url);
    }

    private static string? FromQuery(Uri? url)
    {
        if (url is null)
            return null;

        try
        {
            foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];
                if (Uri.UnescapeDataString(key) != QueryKey)
                    continue;

                var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
                return Uri.UnescapeDataString(value);
            }
        }
        catch (UriFormatException)
        {
            // A malformed query carries no token as far as we are concerned.
        }

        return null;
    }

    private static string? FromCookie(HttpListenerRequest request)
    {
        try
        {
            return request.Cookies[CookieName]?.Value;
        }
        catch (Exception exception) when (exception is CookieException or ArgumentException)
        {
            // A malformed Cookie header is not an authorisation.
            return null;
        }
    }
}
