using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace BaGetter.Core;

public class PackageService : IPackageService
{
    private readonly IPackageDatabase _db;
    private readonly IUpstreamClient _upstream;
    private readonly IPackageIndexingService _indexer;
    private readonly IPackagePolicyEvaluator _policy;
    private readonly ILogger<PackageService> _logger;

    public PackageService(
        IPackageDatabase db,
        IUpstreamClient upstream,
        IPackageIndexingService indexer,
        IPackagePolicyEvaluator policy,
        ILogger<PackageService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<NuGetVersion>> FindPackageVersionsAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var upstreamVersions = await _upstream.ListPackageVersionsAsync(id, cancellationToken);
        upstreamVersions = upstreamVersions
            .Where(version => IsAllowed(id, version, PackageFilterScope.Upstream))
            .ToList();

        // Merge the local package versions into the upstream package versions.
        var localPackages = await _db.FindAsync(id, includeUnlisted: true, cancellationToken);
        var localVersions = localPackages
            .Where(IsAllowed)
            .Select(p => p.Version);

        if (!upstreamVersions.Any()) return localVersions.ToList();
        if (!localPackages.Any()) return upstreamVersions;

        return upstreamVersions.Concat(localVersions).Distinct().ToList();
    }

    public async Task<IReadOnlyList<Package>> FindPackagesAsync(string id, CancellationToken cancellationToken)
    {
        var upstreamPackages = await _upstream.ListPackagesAsync(id, cancellationToken);
        upstreamPackages = upstreamPackages
            .Where(package => IsAllowed(package.Id ?? id, package.Version, PackageFilterScope.Upstream))
            .ToList();

        var localPackages = await _db.FindAsync(id, includeUnlisted: true, cancellationToken);
        localPackages = localPackages.Where(IsAllowed).ToList();

        if (!upstreamPackages.Any()) return localPackages;
        if (!localPackages.Any()) return upstreamPackages;

        // Merge the local packages into the upstream packages.
        var result = upstreamPackages.ToDictionary(p => p.Version);
        var local = localPackages.ToDictionary(p => p.Version);

        foreach (var localPackage in local)
        {
            result[localPackage.Key] = localPackage.Value;
        }

        return result.Values.ToList();
    }

    public async Task<Package> FindPackageOrNullAsync(
        string id,
        NuGetVersion version,
        CancellationToken cancellationToken)
    {
        if (!await MirrorAsync(id, version, cancellationToken))
        {
            return null;
        }

        return await _db.FindOrNullAsync(id, version, includeUnlisted: true, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        return await MirrorAsync(id, version, cancellationToken);
    }

    public async Task AddDownloadAsync(string packageId, NuGetVersion version, CancellationToken cancellationToken)
    {
        await _db.AddDownloadAsync(packageId, version, cancellationToken);
    }

    /// <summary>
    /// Index the package from an upstream if it does not exist locally.
    /// </summary>
    /// <param name="id">The package ID to index from an upstream.</param>
    /// <param name="version">The package version to index from an upstream.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>True if the package exists locally or was indexed from an upstream source.</returns>
    private async Task<bool> MirrorAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var localPackage = await _db.FindOrNullAsync(
            id,
            version,
            includeUnlisted: true,
            cancellationToken);

        if (localPackage != null)
        {
            return IsAllowed(localPackage);
        }

        if (!IsAllowed(id, version, PackageFilterScope.Upstream))
        {
            return false;
        }

        var cacheFeedUrl = _upstream.GetServiceIndexUrl();

        _logger.LogInformation(
            "Package {PackageId} {PackageVersion} does not exist locally. Checking upstream feed ({cacheFeedUrl})...",
            id,
            version,
            cacheFeedUrl);

        try
        {
            using var packageStream = await _upstream.DownloadPackageOrNullAsync(id, version, cancellationToken);
            if (packageStream == null)
            {
                _logger.LogWarning(
                    "Upstream feed does not have package {PackageId} {PackageVersion}",
                    id,
                    version);
                return false;
            }

            _logger.LogInformation(
                "Downloaded package {PackageId} {PackageVersion}, indexing...",
                id,
                version);

            var result = await _indexer.IndexAsync(packageStream, cacheFeedUrl, cancellationToken);

            _logger.LogInformation(
                "Finished indexing package {PackageId} {PackageVersion} from upstream feed with result {Result}",
                id,
                version,
                result);

            return result == PackageIndexingResult.Success;
        }
        catch (Exception e)
        {
            _logger.LogError(
                e,
                "Failed to index package {PackageId} {PackageVersion} from upstream",
                id,
                version);

            return false;
        }
    }

    private bool IsAllowed(Package package)
    {
        var scope = string.IsNullOrEmpty(package.CachedFrom)
            ? PackageFilterScope.Local
            : PackageFilterScope.CachedUpstream;

        return IsAllowed(package.Id, package.Version, scope);
    }

    private bool IsAllowed(string id, NuGetVersion version, PackageFilterScope scope)
    {
        return !_policy.Evaluate(new PackageFilterContext(id, version, scope)).IsBlocked;
    }
}
