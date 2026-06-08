using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
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

    private BaGetterApplication CreateDownstream(
        HttpClient upstreamClient,
        bool enabled = true)
    {
        return new BaGetterApplication(
            _output,
            upstreamClient,
            configuration =>
            {
                configuration["PackageFiltering:Enabled"] = enabled.ToString();
                configuration["PackageFiltering:BlockCachedPackages"] = "true";
                configuration["PackageFiltering:Rules:0:PackageId"] = "TestData";
                configuration["PackageFiltering:Rules:0:Versions"] = "*";
            });
    }
}
