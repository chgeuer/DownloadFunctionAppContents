namespace Downloader
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Http.Json;
    using System.Reflection.Metadata.Ecma335;
    using System.Text;
    using System.Text.Json.Nodes;
    using Azure.Core;
    using Azure.Identity; // <PackageReference Include="Azure.Identity" Version="1.9.0" />

    public record PublishingCredentialProperties(string PublishingUserName, string PublishingPassword, string ScmUri);
    public record PublishingCredential(string Id, string Name, PublishingCredentialProperties Properties);
    public record VirtualFileSystemEntry(string Name, long Size, DateTimeOffset Mtime, DateTimeOffset Crtime, string Mime, string Href, string Path);
    internal record SiteInfo(string TenantID, string SubscriptionID, string ResourceGroupName, string SiteName, string SlotName);
    internal enum FollowPolicy { IgnoreShortcuts = 0, FollowShortcuts = 1 }

    public static class DownloadFunctionAppContents
    {
        public static async Task Main()
        {
            var siteName = "linuxdockerw";

            SiteInfo ISVSite(string ResourceGroupName, string SiteName) => new("geuer-pollmann.de", "706df49f-998b-40ec-aed3-7f0ce9c67759", ResourceGroupName, SiteName, SlotName: null);
            SiteInfo CustomerSite(string ResourceGroupName, string SiteName) => new("chgeuerfte.aad.geuer-pollmann.de", "724467b5-bee4-484b-bf13-d6a5505d2b51", ResourceGroupName, SiteName, SlotName: null);
            List<SiteInfo> siteInfos = new()
            {
                ISVSite("meteredbilling-infra-20230112", "spqpzpz3chwpnb6"),
                CustomerSite("checkpoint", "somefunctionwindows"),
                CustomerSite("checkpoint", "linuxdockerw"),
                CustomerSite("checkpoint", "somebiggerwindowsfunc"),
                CustomerSite("checkpoint", "checkpoint1"),
                CustomerSite("checkpoint", "funcchgeuer123"),
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

            var accessToken = await cred.GetTokenAsync(new(scopes: new[] { "https://management.azure.com/.default" }));
            HttpClient armHttpClient = accessToken.CreateARMHttpClient();

            bool needToDisableBasicAuthAgain = false;
            bool scmBasicAllowed = await armHttpClient.GetSCMBasicAuthEnabled(siteInfo);
            if (!scmBasicAllowed)
            {
                await armHttpClient.SetSCMSetBasicAuth(siteInfo, allow: true);
                // Wait until the updated permission propagated to the SCM site.
                await Task.Delay(TimeSpan.FromSeconds(2));
                needToDisableBasicAuthAgain = true;
            }

            var scmCredential = await armHttpClient.FetchSCMCredential(siteInfo);

            var vfsEndpoint = $"https://{siteInfo.SiteName}.scm.azurewebsites.net/api/vfs/";

            var zipFilename = new FileInfo($"{siteInfo.SiteName}.zip").FullName;
            using var outputStream = File.OpenWrite(zipFilename);
            using ZipArchive zipArchive = new(outputStream, ZipArchiveMode.Create);

            try
            {
                await scmCredential.CreateSCMHttpClient().RecurseAsync(
                    requestUri: vfsEndpoint,
                    policy: FollowPolicy.IgnoreShortcuts,
                    task: zipArchive.CreateEntry);

                await Console.Out.WriteLineAsync($"Created archive {zipFilename}");
            }
            finally
            {
                if (needToDisableBasicAuthAgain)
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

                using var zipArchiveEntryStream = zipArchiveEntry.Open();
                var downloadStream = await client.GetStreamAsync(requestUri: vfsEntry.Href);
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

        internal static HttpClient CreateARMHttpClient(this AccessToken accessToken)
            => new HttpClient() { BaseAddress = new("https://management.azure.com/") }.AddAccessTokenCredential(accessToken);

        internal static HttpClient AddBasicAuthCredential(this HttpClient httpClient, string username, string password)
        {
            var upBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"), Base64FormattingOptions.None);
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {upBase64}");
            return httpClient;
        }

        internal static HttpClient AddAccessTokenAsBasicAuthCredential(this HttpClient httpClient, AccessToken accessToken)
            => httpClient.AddBasicAuthCredential(username: "00000000-0000-0000-0000-000000000000", password: accessToken.Token);

        internal static HttpClient CreateSCMHttpClient(this PublishingCredential cred)
            => new HttpClient().AddBasicAuthCredential(
                username: cred.Properties.PublishingUserName,
                password: cred.Properties.PublishingPassword);

        internal static async Task<PublishingCredential> FetchSCMCredential(this HttpClient armHttpClient, SiteInfo info)
        {
            // Requires action 'Microsoft.Web/sites/config/list/action' (Other : List Web App Security Sensitive Settings: List Web App's security sensitive settings, such as publishing credentials, app settings and connection strings)
            var requestUri = info.CreateURL("config/publishingcredentials/list?api-version=2022-09-01");
            var scmCredentialResponse = await armHttpClient.PostAsync(requestUri, content: null);
            scmCredentialResponse.EnsureSuccessStatusCode();
            return await scmCredentialResponse.Content.ReadFromJsonAsync<PublishingCredential>();
        }

        internal static async Task<bool> GetSCMBasicAuthEnabled(this HttpClient armHttpClient, SiteInfo info)
        {
            // Requires action 'Microsoft.Web/sites/basicPublishingCredentialsPolicies/read'
            var requestUri = info.CreateURL("basicPublishingCredentialsPolicies/scm?api-version=2022-09-01");
            var policyJsonStr = await armHttpClient.GetStringAsync(requestUri);
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
            var response = await scmHttpClient.GetAsync(requestUri);
            if (response == null || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await Console.Error.WriteLineAsync($"Not found: {requestUri}");
                return;
            }

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