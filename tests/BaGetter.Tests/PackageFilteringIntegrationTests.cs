using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NuGet.Packaging;
using NuGet.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace BaGetter.Tests;

public class PackageFilteringIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public PackageFilteringIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task DisabledFilteringAllowsUpstreamDownload()
    {
        using var upstream = new BaGetterApplication(_output);
        using var downstream = CreateDownstream(upstream.CreateClient(), enabled: false);
        using var package = TestResources.GetResourceStream(TestResources.Package);
        await upstream.AddPackageAsync(package);

        using var response = await downstream.CreateClient().GetAsync(
            "v3/package/TestData/1.2.3/TestData.1.2.3.nupkg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BlockedDownloadReturnsNotFound()
    {
        using var upstream = new BaGetterApplication(_output);
        using var downstream = CreateDownstream(upstream.CreateClient());
        using var package = TestResources.GetResourceStream(TestResources.Package);
        await upstream.AddPackageAsync(package);

        using var response = await downstream.CreateClient().GetAsync(
            "v3/package/TestData/1.2.3/TestData.1.2.3.nupkg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BlockedVersionsAreRemovedFromMetadataAndVersionList()
    {
        using var upstream = new BaGetterApplication(_output);
        using var downstream = CreateDownstream(upstream.CreateClient());
        using var package = TestResources.GetResourceStream(TestResources.Package);
        await upstream.AddPackageAsync(package);
        using var client = downstream.CreateClient();

        using var versions = await client.GetAsync("v3/package/TestData/index.json");
        using var metadata = await client.GetAsync("v3/registration/TestData/index.json");

        Assert.Equal(HttpStatusCode.NotFound, versions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
    }

    [Fact]
    public async Task CachedUpstreamPackageIsBlockedFromDownloadAndSearch()
    {
        using var app = CreateDownstream(upstreamClient: null);
        using var package = TestResources.GetResourceStream(TestResources.Package);
        await app.AddCachedPackageAsync(package, "https://api.nuget.org/v3/index.json");
        using var client = app.CreateClient();

        using var download = await client.GetAsync(
            "v3/package/TestData/1.2.3/TestData.1.2.3.nupkg");
        using var search = await client.GetAsync("v3/search");
        var searchContent = await search.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Contains(@"""totalHits"":0", searchContent);
    }

    [Fact]
    public async Task LocalPackageIsNotBlocked()
    {
        using var app = CreateDownstream(upstreamClient: null);
        using var package = TestResources.GetResourceStream(TestResources.Package);
        await app.AddPackageAsync(package);

        using var response = await app.CreateClient().GetAsync(
            "v3/package/TestData/1.2.3/TestData.1.2.3.nupkg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SearchPaginationSkipsBlockedPackages()
    {
        using var app = CreateDownstream(
            upstreamClient: null,
            blockedPackageId: "Example.Blocked");
        using var blockedPackage = CreatePackage("Example.Blocked");
        using var allowedPackage = CreatePackage("Example.Allowed");
        await app.AddCachedPackageAsync(blockedPackage, "https://packages.example/v3/index.json");
        await app.AddPackageAsync(allowedPackage);

        using var response = await app.CreateClient().GetAsync("v3/search?take=1");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(@"""id"":""Example.Allowed""", content);
        Assert.DoesNotContain(@"""id"":""Example.Blocked""", content);
    }

    private BaGetterApplication CreateDownstream(
        HttpClient upstreamClient,
        bool enabled = true,
        string blockedPackageId = "TestData")
    {
        return new BaGetterApplication(
            _output,
            upstreamClient,
            configuration =>
            {
                configuration["PackageFiltering:Enabled"] = enabled.ToString();
                configuration["PackageFiltering:BlockCachedPackages"] = "true";
                configuration["PackageFiltering:Rules:0:PackageId"] = blockedPackageId;
                configuration["PackageFiltering:Rules:0:Versions"] = "*";
            });
    }

    private static MemoryStream CreatePackage(string packageId)
    {
        var builder = new PackageBuilder
        {
            Id = packageId,
            Version = NuGetVersion.Parse("1.0.0"),
            Description = "Generated test package"
        };
        builder.Authors.Add("Test Author");
        builder.Files.Add(new PhysicalPackageFile
        {
            SourcePath = typeof(PackageFilteringIntegrationTests).Assembly.Location,
            TargetPath = "lib/net9.0/Test.dll"
        });

        var stream = new MemoryStream();
        builder.Save(stream);
        stream.Position = 0;
        return stream;
    }
}
