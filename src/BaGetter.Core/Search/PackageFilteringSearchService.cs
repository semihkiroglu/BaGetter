using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BaGetter.Protocol.Models;
using NuGet.Versioning;

namespace BaGetter.Core;

public class PackageFilteringSearchService : IPackageSearchService
{
    private const int MinimumBatchSize = 20;

    private readonly ISearchService _inner;
    private readonly IPackageDatabase _packages;
    private readonly IPackagePolicyEvaluator _policy;
    private readonly ISearchResponseBuilder _responseBuilder;

    public PackageFilteringSearchService(
        ISearchService inner,
        IPackageDatabase packages,
        IPackagePolicyEvaluator policy,
        ISearchResponseBuilder responseBuilder)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(responseBuilder);

        _inner = inner;
        _packages = packages;
        _policy = policy;
        _responseBuilder = responseBuilder;
    }

    public async Task<SearchResponse> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!_policy.IsFilteringEnabled || request.Skip < 0 || request.Take <= 0)
        {
            return await _inner.SearchAsync(request, cancellationToken);
        }

        var targetCount = GetTargetCount(request.Skip, request.Take);
        var registrations = new List<PackageRegistration>();
        var seenPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceSkip = 0;
        var batchSize = Math.Max(request.Take, MinimumBatchSize);

        while (registrations.Count < targetCount)
        {
            var response = await _inner.SearchAsync(
                Clone(request, sourceSkip, batchSize),
                cancellationToken);
            var results = response.Data ?? Array.Empty<SearchResult>();

            if (results.Count == 0)
            {
                break;
            }

            var discoveredPackage = false;
            foreach (var result in results)
            {
                if (!seenPackageIds.Add(result.PackageId))
                {
                    continue;
                }

                discoveredPackage = true;
                var registration = await GetAllowedRegistrationAsync(result, cancellationToken);
                if (registration != null)
                {
                    registrations.Add(registration);
                }

                if (registrations.Count == targetCount)
                {
                    break;
                }
            }

            sourceSkip += results.Count;
            if (results.Count < batchSize || !discoveredPackage)
            {
                break;
            }
        }

        return _responseBuilder.BuildSearch(
            registrations.Skip(request.Skip).Take(request.Take).ToList());
    }

    public async Task<AutocompleteResponse> AutocompleteAsync(
        AutocompleteRequest request,
        CancellationToken cancellationToken)
    {
        if (!_policy.IsFilteringEnabled || request.Skip < 0 || request.Take <= 0)
        {
            return await _inner.AutocompleteAsync(request, cancellationToken);
        }

        var targetCount = GetTargetCount(request.Skip, request.Take);
        var allowedPackageIds = new List<string>();
        var seenPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceSkip = 0;
        var batchSize = Math.Max(request.Take, MinimumBatchSize);

        while (allowedPackageIds.Count < targetCount)
        {
            var response = await _inner.AutocompleteAsync(
                Clone(request, sourceSkip, batchSize),
                cancellationToken);
            var packageIds = response.Data ?? Array.Empty<string>();

            if (packageIds.Count == 0)
            {
                break;
            }

            var discoveredPackage = false;
            foreach (var packageId in packageIds)
            {
                if (!seenPackageIds.Add(packageId))
                {
                    continue;
                }

                discoveredPackage = true;
                var packages = await _packages.FindAsync(
                    packageId,
                    includeUnlisted: false,
                    cancellationToken);

                if (packages.Any(IsAllowed))
                {
                    allowedPackageIds.Add(packageId);
                }

                if (allowedPackageIds.Count == targetCount)
                {
                    break;
                }
            }

            sourceSkip += packageIds.Count;
            if (packageIds.Count < batchSize || !discoveredPackage)
            {
                break;
            }
        }

        return _responseBuilder.BuildAutocomplete(
            allowedPackageIds.Skip(request.Skip).Take(request.Take).ToList());
    }

    public async Task<AutocompleteResponse> ListPackageVersionsAsync(
        VersionsRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _inner.ListPackageVersionsAsync(request, cancellationToken);
        if (!_policy.IsFilteringEnabled)
        {
            return response;
        }

        var packages = await _packages.FindAsync(
            request.PackageId,
            includeUnlisted: false,
            cancellationToken);
        var allowedVersions = packages
            .Where(IsAllowed)
            .Select(package => package.Version)
            .ToHashSet(VersionComparer.VersionReleaseMetadata);
        var versions = (response.Data ?? Array.Empty<string>())
            .Where(version =>
                NuGetVersion.TryParse(version, out var parsedVersion) &&
                allowedVersions.Contains(parsedVersion))
            .ToList();

        return _responseBuilder.BuildAutocomplete(versions);
    }

    public Task<DependentsResponse> FindDependentsAsync(
        string packageId,
        CancellationToken cancellationToken)
        => _inner.FindDependentsAsync(packageId, cancellationToken);

    private async Task<PackageRegistration> GetAllowedRegistrationAsync(
        SearchResult result,
        CancellationToken cancellationToken)
    {
        var visibleVersions = GetVisibleVersions(result);
        if (visibleVersions.Count == 0)
        {
            return null;
        }

        var packages = await _packages.FindAsync(
            result.PackageId,
            includeUnlisted: false,
            cancellationToken);
        var allowedPackages = packages
            .Where(package => visibleVersions.Contains(package.Version))
            .Where(IsAllowed)
            .ToList();

        return allowedPackages.Count == 0
            ? null
            : new PackageRegistration(result.PackageId, allowedPackages);
    }

    private bool IsAllowed(Package package)
    {
        var scope = string.IsNullOrEmpty(package.CachedFrom)
            ? PackageFilterScope.Local
            : PackageFilterScope.CachedUpstream;

        return !_policy.Evaluate(
            new PackageFilterContext(package.Id, package.Version, scope)).IsBlocked;
    }

    private static HashSet<NuGetVersion> GetVisibleVersions(SearchResult result)
    {
        var versions = new HashSet<NuGetVersion>(VersionComparer.VersionReleaseMetadata);

        foreach (var item in result.Versions ?? Array.Empty<SearchResultVersion>())
        {
            if (NuGetVersion.TryParse(item.Version, out var version))
            {
                versions.Add(version);
            }
        }

        if (versions.Count == 0 && NuGetVersion.TryParse(result.Version, out var latestVersion))
        {
            versions.Add(latestVersion);
        }

        return versions;
    }

    private static int GetTargetCount(int skip, int take)
        => (int)Math.Min(int.MaxValue, (long)skip + take);

    private static SearchRequest Clone(SearchRequest request, int skip, int take)
        => new()
        {
            Skip = skip,
            Take = take,
            IncludePrerelease = request.IncludePrerelease,
            IncludeSemVer2 = request.IncludeSemVer2,
            PackageType = request.PackageType,
            Framework = request.Framework,
            Query = request.Query
        };

    private static AutocompleteRequest Clone(AutocompleteRequest request, int skip, int take)
        => new()
        {
            Skip = skip,
            Take = take,
            IncludePrerelease = request.IncludePrerelease,
            IncludeSemVer2 = request.IncludeSemVer2,
            PackageType = request.PackageType,
            Query = request.Query
        };
}
