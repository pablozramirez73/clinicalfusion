namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Copia i cookie della WebView (es. <c>CF_Authorization</c> dopo il login Cloudflare Access) nello
/// store usato da HttpClient: su iOS WKWebView e NSURLSession hanno archivi cookie separati.
/// </summary>
public interface IWebSessionBridge
{
    Task<int> SyncCookiesAsync(Uri forHost);

    Task ClearAsync();
}
