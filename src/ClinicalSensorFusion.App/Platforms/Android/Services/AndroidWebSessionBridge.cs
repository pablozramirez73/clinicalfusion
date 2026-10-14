using System.Net;
using Android.Webkit;
using ClinicalSensorFusion.App.Services;

namespace ClinicalSensorFusion.App.Platforms.Android.Services;

/// <summary>
/// Copia i cookie della WebView (es. <c>CF_Authorization</c> di Cloudflare Access) nel
/// <see cref="CookieContainer"/> condiviso dai client HTTP dell'app: su Android WebView e HttpClient hanno
/// archivi cookie separati.
/// </summary>
internal sealed class AndroidWebSessionBridge(CookieContainer cookies) : IWebSessionBridge
{
    public Task<int> SyncCookiesAsync(Uri forHost)
    {
        // CookieManager restituisce i cookie validi per l'indirizzo come "nome=valore; nome=valore".
        var header = CookieManager.Instance?.GetCookie(forHost.AbsoluteUri);
        if (string.IsNullOrWhiteSpace(header))
        {
            return Task.FromResult(0);
        }

        var root = new Uri(forHost.GetLeftPart(UriPartial.Authority));
        var copied = 0;
        foreach (var pair in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            try
            {
                cookies.Add(root, new Cookie(pair[..separator], pair[(separator + 1)..], "/"));
                copied++;
            }
            catch (CookieException)
            {
                // Valore con caratteri che .NET non accetta in un cookie: non serve all'accesso, si salta.
            }
        }

        return Task.FromResult(copied);
    }

    public Task ClearAsync()
    {
        CookieManager.Instance?.RemoveAllCookies(null);
        CookieManager.Instance?.Flush();
        WebStorage.Instance?.DeleteAllData();
        foreach (Cookie cookie in cookies.GetAllCookies())
        {
            cookie.Expired = true;
        }

        return Task.CompletedTask;
    }
}
