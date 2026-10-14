using ClinicalSensorFusion.App.Services;
using Foundation;
using WebKit;

namespace ClinicalSensorFusion.App.Platforms.iOS.Services;

/// <summary>
/// Copia i cookie di WKWebView (es. <c>CF_Authorization</c> di Cloudflare Access) in NSHTTPCookieStorage,
/// usato da NSUrlSessionHandler, il gestore HTTP predefinito di .NET su iOS.
/// </summary>
internal sealed class IosWebSessionBridge : IWebSessionBridge
{
    public async Task<int> SyncCookiesAsync(Uri forHost)
    {
        var store = WKWebsiteDataStore.DefaultDataStore.HttpCookieStore;
        var cookies = await store.GetAllCookiesAsync();
        var shared = NSHttpCookieStorage.SharedStorage;
        var copied = 0;
        foreach (var cookie in cookies)
        {
            var domain = cookie.Domain.TrimStart('.');
            if (forHost.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                forHost.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                shared.SetCookie(cookie);
                copied++;
            }
        }

        return copied;
    }

    public async Task ClearAsync()
    {
        var types = WKWebsiteDataStore.AllWebsiteDataTypes;
        await WKWebsiteDataStore.DefaultDataStore.RemoveDataOfTypesAsync(types, NSDate.DistantPast);
        foreach (var cookie in NSHttpCookieStorage.SharedStorage.Cookies)
        {
            NSHttpCookieStorage.SharedStorage.DeleteCookie(cookie);
        }
    }
}
