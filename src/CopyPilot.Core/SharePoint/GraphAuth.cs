using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CopyPilot.Core.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace CopyPilot.Core.SharePoint;

/// <summary>Supplies access tokens for Microsoft Graph and for SharePoint's own REST API.</summary>
public interface IGraphTokenProvider
{
    /// <param name="resource">"https://graph.microsoft.com" or a SharePoint host such as "https://contoso.sharepoint.com".</param>
    Task<string> GetAccessTokenAsync(string resource, bool forceRefresh, CancellationToken ct);
}

/// <summary>
/// Signs in to Microsoft Entra ID with MSAL: as a user (system browser or device code; delegated Graph
/// Sites.ReadWrite.All, plus SharePoint AllSites.Write when list item metadata is preserved) or as the app itself
/// (client secret / certificate, application permissions such as Sites.Selected or Sites.ReadWrite.All). Client applications are reused for the lifetime of the process,
/// and user tokens are cached encrypted on disk (DPAPI on Windows), so users sign in once.
/// </summary>
public sealed class MsalGraphTokenProvider(LocationSpec spec, string? userTokenCacheFile, Action<string> log) : IGraphTokenProvider
{
    public const string GraphResource = "https://graph.microsoft.com";

    /// <summary>Delegated scopes: Graph for files, SharePoint AllSites.Write for list item fields (Created, Author, …).</summary>
    public static string[] DelegatedScopes(string resource) =>
        resource == GraphResource ? [$"{GraphResource}/Sites.ReadWrite.All"] : [$"{resource.TrimEnd('/')}/AllSites.Write"];

    public static string[] ApplicationScopes(string resource) => [$"{resource.TrimEnd('/')}/.default"];

    private static readonly ConcurrentDictionary<string, Lazy<Task<IClientApplicationBase>>> Apps = new();

    // Only one interactive sign-in at a time (source and destination can both be SharePoint).
    private static readonly SemaphoreSlim SignInGate = new(1, 1);

    public async Task<string> GetAccessTokenAsync(string resource, bool forceRefresh, CancellationToken ct)
    {
        var app = await Apps.GetOrAdd(Key(), _ => new Lazy<Task<IClientApplicationBase>>(CreateAppAsync)).Value.ConfigureAwait(false);
        if (app is IConfidentialClientApplication cca)
        {
            var r = await cca.AcquireTokenForClient(ApplicationScopes(resource)).WithForceRefresh(forceRefresh).ExecuteAsync(ct).ConfigureAwait(false);
            return r.AccessToken;
        }

        var scopes = DelegatedScopes(resource);

        var pca = (IPublicClientApplication)app;
        await SignInGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = (await pca.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            if (account != null)
            {
                try
                {
                    var silent = await pca.AcquireTokenSilent(scopes, account).WithForceRefresh(forceRefresh)
                        .ExecuteAsync(ct).ConfigureAwait(false);
                    return silent.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // fall through to an interactive sign-in
                }
            }

            AuthenticationResult result;
            if (spec.SharePointAuth == SharePointAuthMode.DeviceCode)
            {
                result = await pca.AcquireTokenWithDeviceCode(scopes, code =>
                {
                    log("ACTION REQUIRED: " + code.Message);
                    return Task.CompletedTask;
                }).ExecuteAsync(ct).ConfigureAwait(false);
            }
            else
            {
                log("INFO: A browser window opens to sign in to Microsoft 365 …");
                result = await pca.AcquireTokenInteractive(scopes)
                    .WithUseEmbeddedWebView(false)
                    .WithPrompt(Prompt.SelectAccount)
                    .ExecuteAsync(ct).ConfigureAwait(false);
            }

            log($"INFO: Signed in to Microsoft 365 as {result.Account?.Username}");
            return result.AccessToken;
        }
        finally
        {
            SignInGate.Release();
        }
    }

    private string Tenant => string.IsNullOrWhiteSpace(spec.TenantId) ? "organizations" : spec.TenantId.Trim();

    private string Authority => $"https://login.microsoftonline.com/{Tenant}";

    private string Key()
    {
        var secret = spec.SharePointAuth switch
        {
            SharePointAuthMode.ClientSecret => spec.ClientSecret,
            SharePointAuthMode.Certificate => spec.CertificatePath + "|" + spec.CertificatePassword,
            _ => "",
        };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var mode = spec.SharePointAuth is SharePointAuthMode.Interactive or SharePointAuthMode.DeviceCode ? "user" : spec.SharePointAuth.ToString();
        return $"{mode}|{Tenant}|{spec.ClientId.Trim()}|{hash}".ToLowerInvariant();
    }

    private async Task<IClientApplicationBase> CreateAppAsync()
    {
        var clientId = spec.ClientId.Trim();
        switch (spec.SharePointAuth)
        {
            case SharePointAuthMode.ClientSecret:
                return ConfidentialClientApplicationBuilder.Create(clientId).WithAuthority(Authority)
                    .WithClientSecret(spec.ClientSecret).Build();
            case SharePointAuthMode.Certificate:
            {
#pragma warning disable SYSLIB0057 // X509CertificateLoader is .NET 9+; this project targets .NET 8.
                var cert = new X509Certificate2(spec.CertificatePath.Trim(),
                    string.IsNullOrEmpty(spec.CertificatePassword) ? null : spec.CertificatePassword,
                    X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
                return ConfidentialClientApplicationBuilder.Create(clientId).WithAuthority(Authority).WithCertificate(cert).Build();
            }
            default:
            {
                var pca = PublicClientApplicationBuilder.Create(clientId).WithAuthority(Authority)
                    .WithRedirectUri("http://localhost").Build();
                if (userTokenCacheFile != null && OperatingSystem.IsWindows())
                {
                    try
                    {
                        var props = new StorageCreationPropertiesBuilder(Path.GetFileName(userTokenCacheFile),
                            Path.GetDirectoryName(userTokenCacheFile)!).Build();
                        var helper = await MsalCacheHelper.CreateAsync(props).ConfigureAwait(false);
                        helper.RegisterCache(pca.UserTokenCache);
                    }
                    catch (Exception ex)
                    {
                        log($"WARNING: the Microsoft 365 sign-in cannot be remembered ({ex.Message}); you may be asked to sign in again.");
                    }
                }

                return pca;
            }
        }
    }
}
