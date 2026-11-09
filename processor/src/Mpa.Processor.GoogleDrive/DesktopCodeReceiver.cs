using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;

namespace Mpa.Processor.GoogleDrive;

public interface IAuthorizationBrowser
{
    void Open(Uri authorizationUri);
}

public sealed class SystemAuthorizationBrowser : IAuthorizationBrowser
{
    public void Open(Uri authorizationUri)
    {
        if (authorizationUri.Scheme != "https" || authorizationUri.Host != "accounts.google.com")
            throw new InvalidDataException("Unexpected Google authorization destination.");
        using var process = Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true });
        // The system browser belongs to the user, not to this application. Never terminate it.
    }
}

public sealed class DesktopCodeReceiver(IAuthorizationBrowser browser) : ICodeReceiver
{
    private readonly BrowserReceiver _receiver = new(browser);
    public string RedirectUri => _receiver.RedirectUri;
    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken taskCancellationToken)
    {
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        url.State = state;
        if (url is GoogleAuthorizationCodeRequestUrl google)
        {
            google.Prompt = "consent";
            google.IncludeGrantedScopes = "false";
        }
        var response = await _receiver.ReceiveCodeAsync(url, taskCancellationToken).ConfigureAwait(false);
        if (response.State is not { Length: <= 128 } || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(response.State)))
            throw new InvalidDataException("Google authorization response does not match the outstanding request.");
        return response;
    }
    private sealed class BrowserReceiver(IAuthorizationBrowser browser) : LocalServerCodeReceiver(
        "<html><head><title>MPA authorization</title></head><body>Return to My Photos Anywhere Processor. You can close this page.</body></html>",
        CallbackUriChooserStrategy.ForceLoopbackIp)
    {
        protected override bool OpenBrowser(string url) { browser.Open(new Uri(url)); return true; }
    }
}
