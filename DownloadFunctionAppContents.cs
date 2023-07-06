namespace Downloader
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Http.Json;
    using System.Text;
    using System.Text.Json.Nodes;
    using Azure.Core;
    using Azure.Identity; // <PackageReference Include="Azure.Identity" Version="1.9.0" />

    public record PublishingCredentialProperties(string PublishingUserName, string PublishingPassword, string ScmUri);
    public record PublishingCredential(string Id, string Name, PublishingCredentialProperties Properties);
    public record VirtualFileSystemEntry(string Name, long Size, DateTimeOffset Mtime, DateTimeOffset Crtime, string Mime, string Href, string Path);
    internal record SiteInfo(string TenantID, string SubscriptionID, string ResourceGroupName, string SiteName, string SlotName);
    internal enum FollowPolicy { IgnoreShortcuts = 0, FollowShortcuts = 1 }
    internal enum SCMAuthenticationMechanism { UseSCMApplicationScope = 0, UseAccessToken = 1}

    public static class DownloadFunctionAppContents
    {
        public static async Task Main()
        {
            string siteName = "downloadcontentdemo";
            var authMechanism = SCMAuthenticationMechanism.UseSCMApplicationScope;

            SiteInfo ISVSite(string resourceGroupName, string siteName) => new(TenantID: "geuer-pollmann.de", SubscriptionID: "706df49f-998b-40ec-aed3-7f0ce9c67759", ResourceGroupName: resourceGroupName, SiteName: siteName, SlotName: null);
            SiteInfo CustomerSite(string resourceGroupName, string siteName) => new(TenantID: "chgeuerfte.aad.geuer-pollmann.de", SubscriptionID: "724467b5-bee4-484b-bf13-d6a5505d2b51", ResourceGroupName: resourceGroupName, SiteName: siteName, SlotName: null);

            List<SiteInfo> siteInfos = new()
            {
                ISVSite("meteredbilling-infra-20230112", "spqpzpz3chwpnb6"),
                CustomerSite("downloadfunctionappcontent", "downloadcontentdemo"),
            };
            SiteInfo siteInfo = siteInfos.Single(si => si.SiteName == siteName);

            var (clientId, clientSecretFile) = ("7b8e9825-af72-4c2a-a2df-94929355b3b8", @"C:\Users\chgeuer\.secrets\principal-for-unencrypted-function-scanning.txt");

            // https://learn.microsoft.com/en-us/dotnet/api/overview/azure/identity-readme?view=azure-dotnet
            // DefaultAzureCredential cred = new();
            // AzureCliCredential cred = new();
            ClientSecretCredential cred = new(
                tenantId: siteInfo.TenantID,
                clientId: clientId,
                clientSecret: await File.ReadAllTextAsync(path: clientSecretFile));

            AccessToken accessToken = await cred.GetTokenAsync(new(scopes: new[] { "https://management.azure.com/.default" }));
            HttpClient armHttpClient = accessToken.CreateARMHttpClient();

            bool needToDisableSCMBasicAuthAgain = false;
            if (authMechanism == SCMAuthenticationMechanism.UseSCMApplicationScope)
            {
                bool scmBasicAuthAllowed = await armHttpClient.GetSCMBasicAuthAllowed(siteInfo);
                if (!scmBasicAuthAllowed)
                {
                    await armHttpClient.SetSCMSetBasicAuth(siteInfo, allow: true);
                    // Wait until the updated permission propagated to the SCM site.
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    needToDisableSCMBasicAuthAgain = true;
                }
            }

            string vfsEndpoint = $"https://{siteInfo.SiteName}.scm.azurewebsites.net/api/vfs/";

            string zipFilename = new FileInfo($"{siteInfo.SiteName}.zip").FullName;
            using Stream outputStream = File.OpenWrite(zipFilename);
            using ZipArchive zipArchive = new(outputStream, ZipArchiveMode.Create);

            try
            {
                HttpClient scmClient = authMechanism switch
                {
                    SCMAuthenticationMechanism.UseSCMApplicationScope => (await armHttpClient.FetchSCMCredential(siteInfo)).CreateSCMHttpClient(),
                    SCMAuthenticationMechanism.UseAccessToken => accessToken.CreateSCMHttpClient(),
                    _ => throw new NotSupportedException(),
                };

                await scmClient.RecurseAsync(
                    requestUri: vfsEndpoint,
                    policy: FollowPolicy.IgnoreShortcuts,
                    task: zipArchive.CreateEntry);

                await Console.Out.WriteLineAsync($"Created archive {zipFilename}");
            }
            finally
            {
                if (needToDisableSCMBasicAuthAgain)
                {
                    await armHttpClient.SetSCMSetBasicAuth(siteInfo, allow: false);
                }
            }
        }

        static async Task CreateEntry(this ZipArchive zipArchive, HttpClient client, VirtualFileSystemEntry vfsEntry)
        {
            await Console.Out.WriteLineAsync($"Adding {vfsEntry.Path}");

            try
            {
                var zipArchiveEntry = zipArchive.CreateEntry(
                    entryName: vfsEntry.Path.Replace(@"C:\", ""),
                    compressionLevel: CompressionLevel.Optimal);
                zipArchiveEntry.LastWriteTime = vfsEntry.Crtime;

                using Stream zipArchiveEntryStream = zipArchiveEntry.Open();
                using Stream downloadStream = await client.GetStreamAsync(requestUri: vfsEntry.Href);
                await downloadStream.CopyToAsync(zipArchiveEntryStream);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"{vfsEntry.Href} {vfsEntry.Name} {ex.Message}");
            }
        }

        internal static string CreateURL(this SiteInfo info, string suffix)
            => string.IsNullOrEmpty(info.SlotName)
                ? $"/subscriptions/{info.SubscriptionID}/resourceGroups/{info.ResourceGroupName}/providers/Microsoft.Web/sites/{info.SiteName}/{suffix}"
                : $"/subscriptions/{info.SubscriptionID}/resourceGroups/{info.ResourceGroupName}/providers/Microsoft.Web/sites/{info.SiteName}/slots/{info.SlotName}/{suffix}";

        internal static HttpClient AddAccessTokenCredential(this HttpClient httpClient, AccessToken accessToken)
        {
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken.Token}");
            return httpClient;
        }

        internal static HttpClient AddBasicAuthCredential(this HttpClient httpClient, string username, string password)
        {
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"), Base64FormattingOptions.None)}");
            return httpClient;
        }

        internal static HttpClient AddAccessTokenAsBasicAuthCredential(this HttpClient httpClient, AccessToken accessToken)
            => httpClient.AddBasicAuthCredential(username: "00000000-0000-0000-0000-000000000000", password: accessToken.Token);

        internal static HttpClient CreateARMHttpClient(this AccessToken accessToken)
            => new HttpClient() { BaseAddress = new("https://management.azure.com/") }.AddAccessTokenCredential(accessToken);

        internal static HttpClient CreateSCMHttpClient(this AccessToken accessToken)
            => new HttpClient().AddAccessTokenAsBasicAuthCredential(accessToken);

        internal static HttpClient CreateSCMHttpClient(this PublishingCredential cred)
            => new HttpClient().AddBasicAuthCredential(
                username: cred.Properties.PublishingUserName,
                password: cred.Properties.PublishingPassword);

        internal static async Task<PublishingCredential> FetchSCMCredential(this HttpClient armHttpClient, SiteInfo info)
        {
            // Requires action 'Microsoft.Web/sites/config/list/action'
            // (Other : List Web App Security Sensitive Settings: List Web App's security sensitive settings, such as publishing credentials, app settings and connection strings)
            string requestUri = info.CreateURL("config/publishingcredentials/list?api-version=2022-09-01");
            HttpResponseMessage scmCredentialResponse = await armHttpClient.PostAsync(requestUri, content: null);
            scmCredentialResponse.EnsureSuccessStatusCode();
            return await scmCredentialResponse.Content.ReadFromJsonAsync<PublishingCredential>();
        }

        internal static async Task<bool> GetSCMBasicAuthAllowed(this HttpClient armHttpClient, SiteInfo info)
        {
            // Requires action 'Microsoft.Web/sites/basicPublishingCredentialsPolicies/read'
            string requestUri = info.CreateURL("basicPublishingCredentialsPolicies/scm?api-version=2022-09-01");
            string policyJsonStr = await armHttpClient.GetStringAsync(requestUri);
            JsonNode json = JsonNode.Parse(policyJsonStr)!;
            return (bool)json["properties"]["allow"];
        }

        internal static async Task SetSCMSetBasicAuth(this HttpClient armHttpClient, SiteInfo info, bool allow)
        {
            // az resource update \
            //    --subscription "${subscriptionId}" --resource-group "${resourceGroupName}" \
            //    --namespace Microsoft.Web --parent "sites/${siteName}" \
            //    --resource-type basicPublishingCredentialsPolicies --name scm --set properties.allow=true

            var requestUri = info.CreateURL("basicPublishingCredentialsPolicies/scm?api-version=2022-09-01");

            // Requires action 'Microsoft.Web/sites/basicPublishingCredentialsPolicies/scm/Read' and 'Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies/scm/Read'
            var policyJsonStr = await armHttpClient.GetStringAsync(requestUri);
            JsonNode json = JsonNode.Parse(policyJsonStr)!;
            if (allow != (bool)json["properties"]["allow"])
            {
                json["properties"]["allow"] = allow;

                // Requires 'Microsoft.Web/sites/basicPublishingCredentialsPolicies/write'
                await armHttpClient.PutAsync(requestUri, new StringContent(
                    content: json.ToJsonString(),
                    encoding: Encoding.UTF8,
                    mediaType: "application/json"));
            }
        }

        internal static async Task RecurseAsync(this HttpClient scmHttpClient, string requestUri, FollowPolicy policy, Func<HttpClient, VirtualFileSystemEntry, Task> task)
        {
            HttpResponseMessage response = await scmHttpClient.GetAsync(requestUri);
            if (response == null || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await Console.Error.WriteLineAsync($"Not found: {requestUri}");
                return;
            }
            response.EnsureSuccessStatusCode();

            var entries = await response.Content.ReadFromJsonAsync<IEnumerable<VirtualFileSystemEntry>>();
            foreach (var entry in entries)
            {
                await ((entry.Mime, policy) switch
                {
                    ("inode/directory", _) => scmHttpClient.RecurseAsync(entry.Href, policy, task),
                    ("inode/shortcut", FollowPolicy.FollowShortcuts) => scmHttpClient.RecurseAsync(entry.Href, policy, task),
                    ("inode/shortcut", FollowPolicy.IgnoreShortcuts) => Task.CompletedTask,
                    _ => task(scmHttpClient, entry)
                });
            }
        }
    }
}