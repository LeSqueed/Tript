// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Tript.App;

internal sealed class SessionToken
{
    internal const string QueryKey = "k";

    internal const string CookieName = "tript_session";

    private const int TokenBytes = 32;

    private readonly byte[] _expected;

    internal SessionToken()
    {
        Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
        _expected = Encoding.UTF8.GetBytes(Value);
    }

    internal string Value { get; }

    internal string UiUrl => BuildUiUrl(LocalPorts.Ui);

    internal string BuildUiUrl(int port) => $"http://localhost:{port}/?{QueryKey}={Value}";

    internal bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;

        return CryptographicOperations.FixedTimeEquals(_expected, Encoding.UTF8.GetBytes(presented));
    }

    internal bool Authorises(HttpListenerRequest request, bool acceptCookie = false)
    {
        if (Matches(FromQuery(request)))
            return true;

        return acceptCookie && Matches(FromCookie(request));
    }

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
            return null;
        }
    }
}
