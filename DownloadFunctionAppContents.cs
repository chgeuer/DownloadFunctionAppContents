﻿namespace Downloader
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Http.Json;
    using System.Text;
    using Azure.Identity; // <PackageReference Include="Azure.Identity" Version="1.9.0" />

    public record PublishingCredentialProperties(string PublishingUserName, string PublishingPassword, string ScmUri);
    public record PublishingCredential(string Id, string Name, PublishingCredentialProperties Properties);
    public record VirtualFileSystemEntry(string Name, long Size, DateTimeOffset Mtime, DateTimeOffset Crtime, string Mime, string Href, string Path);
    public enum FollowPolicy { IgnoreShortcuts = 0, FollowShortcuts = 1 }

    public static class DownloadFunctionAppContents
    {
        public static async Task Main()
        {
            //DefaultAzureCredential cred = new();
            //AzureCliCredential cred = new();
            ClientSecretCredential cred = new(
                tenantId: "5f9e748d-300b-48f1-85f5-3aa96d6260cb",
                clientId: "7b8e9825-af72-4c2a-a2df-94929355b3b8",
                clientSecret: await File.ReadAllTextAsync(path: @"C:\Users\chgeuer\.secrets\principal-for-unencrypted-function-scanning.txt"));

            var accessToken = await cred.GetTokenAsync(new(scopes: new[] { "https://management.azure.com/.default" }));

            var scmCredential = await FetchSCMCredentialFromAzureResourceManager(
                bearerToken: accessToken.Token,
                subscriptionId: "706df49f-998b-40ec-aed3-7f0ce9c67759",
                resourceGroupName: "meteredbilling-infra-20230112",
                siteName: "spqpzpz3chwpnb6");

            var zipFilename = new FileInfo($"{scmCredential.Name}.zip").FullName;
            using var outputStream = File.OpenWrite(zipFilename);
            using ZipArchive zipArchive = new(outputStream, ZipArchiveMode.Create);

            await scmCredential.CreateHttpClient().RecurseAsync(
                requestUri: $"https://{scmCredential.Name}.scm.azurewebsites.net/api/vfs/",
                policy: FollowPolicy.IgnoreShortcuts, task: zipArchive.CreateEntry);

            await Console.Out.WriteLineAsync($"Created archive {zipFilename}");
        }

        static async Task CreateEntry(this ZipArchive zipArchive, HttpClient client, VirtualFileSystemEntry vfsEntry)
        {
            await Console.Out.WriteLineAsync($"Adding {vfsEntry.Path}");

            try
            {
                var zipEntry = zipArchive.CreateEntry(
                    entryName: vfsEntry.Path.Replace(@"C:\", ""),
                    compressionLevel: CompressionLevel.Optimal);
                zipEntry.LastWriteTime = vfsEntry.Crtime;

                using var zipEntryStream = zipEntry.Open();
                var downloadStream = await client.GetStreamAsync(requestUri: vfsEntry.Href);
                await downloadStream.CopyToAsync(zipEntryStream);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"{vfsEntry.Href} {vfsEntry.Name} {ex.Message}");
            }
        }

        public static async Task<PublishingCredential> FetchSCMCredentialFromAzureResourceManager(string bearerToken, string subscriptionId, string resourceGroupName, string siteName, string slotName = null)
        {
            HttpClient httpClient = new() { BaseAddress = new("https://management.azure.com/") };
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearerToken}");

            var listPublishingPath = string.IsNullOrEmpty(slotName)
                ? $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Web/sites/{siteName}/config/publishingcredentials/list?api-version=2022-09-01"
                : $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Web/sites/{siteName}/slots/{slotName}/config/publishingcredentials/list?api-version=2022-09-01";
            var scmCredentialResponse = await httpClient.PostAsync(requestUri: listPublishingPath, content: null);
            scmCredentialResponse.EnsureSuccessStatusCode();
            return await scmCredentialResponse.Content.ReadFromJsonAsync<PublishingCredential>();
        }

        public static HttpClient CreateHttpClient(this PublishingCredential scmCredential)
        {
            HttpClient downloaderClient = new();
            var up = $"{scmCredential!.Properties.PublishingUserName}:{scmCredential.Properties.PublishingPassword}";
            var basicUsernamePassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(up), Base64FormattingOptions.None);
            downloaderClient.DefaultRequestHeaders.Add("Authorization", $"Basic {basicUsernamePassword}");
            return downloaderClient;
        }

        public static async Task RecurseAsync(this HttpClient httpClient, string requestUri, FollowPolicy policy, Func<HttpClient, VirtualFileSystemEntry, Task> task)
        {
            var entries = await httpClient.GetFromJsonAsync<IEnumerable<VirtualFileSystemEntry>>(requestUri);
            foreach (var entry in entries)
            {
                await ((entry.Mime, policy) switch
                {
                    ("inode/directory", _) => httpClient.RecurseAsync(entry.Href, policy, task),
                    ("inode/shortcut", FollowPolicy.FollowShortcuts) => httpClient.RecurseAsync(entry.Href, policy, task),
                    ("inode/shortcut", FollowPolicy.IgnoreShortcuts) => Task.CompletedTask,
                    _ => task(httpClient, entry)
                });
            }
        }
    }
}