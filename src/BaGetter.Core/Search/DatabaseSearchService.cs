using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BaGetter.Protocol.Models;
using Microsoft.EntityFrameworkCore;

namespace BaGetter.Core;

public class DatabaseSearchService : ISearchService
{
    private readonly IContext _context;
    private readonly IFrameworkCompatibilityService _frameworks;
    private readonly IPackagePolicyEvaluator _policy;
    private readonly ISearchResponseBuilder _searchBuilder;

    public DatabaseSearchService(
        IContext context,
        IFrameworkCompatibilityService frameworks,
        ISearchResponseBuilder searchBuilder,
        IPackagePolicyEvaluator policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(frameworks);
        ArgumentNullException.ThrowIfNull(searchBuilder);
        ArgumentNullException.ThrowIfNull(policy);

        _context = context;
        _frameworks = frameworks;
        _searchBuilder = searchBuilder;
        _policy = policy;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var frameworks = GetCompatibleFrameworksOrNull(request.Framework);

        IQueryable<Package> search = _context.Packages;
        search = ApplySearchQuery(search, request.Query);
        search = ApplySearchFilters(
            search,
            request.IncludePrerelease,
            request.IncludeSemVer2,
            request.PackageType,
            frameworks);

        if (_policy.IsFilteringEnabled)
        {
            var packageIds = await GetAllowedPackageIdsAsync(
                search.Select(p => p.Id).Distinct().OrderBy(id => id),
                search,
                request.Skip,
                request.Take,
                cancellationToken);

            if (packageIds.Count == 0)
            {
                return _searchBuilder.BuildSearch(new List<PackageRegistration>());
            }

            search = _context.Packages.Where(p => packageIds.Contains(p.Id));
        }
        else
        {
            var packageIds = search
                .Select(p => p.Id)
                .Distinct()
                .OrderBy(id => id)
                .Skip(request.Skip)
                .Take(request.Take);

            if (_context.SupportsLimitInSubqueries)
            {
                search = _context.Packages.Where(p => packageIds.Contains(p.Id));
            }
            else
            {
                var packageIdResults = await packageIds.ToListAsync(cancellationToken);
                search = _context.Packages.Where(p => packageIdResults.Contains(p.Id));
            }
        }

        // Fetch every matching version for the selected package IDs so the latest version is correct.
        search = ApplySearchFilters(
            search,
            request.IncludePrerelease,
            request.IncludeSemVer2,
            request.PackageType,
            frameworks);

        var results = await search.ToListAsync(cancellationToken);
        if (_policy.IsFilteringEnabled)
        {
            results = results.Where(IsAllowed).ToList();
        }
        var groupedResults = results
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PackageRegistration(group.Key, group.ToList()))
            .ToList();

        return _searchBuilder.BuildSearch(groupedResults);
    }

    public async Task<AutocompleteResponse> AutocompleteAsync(AutocompleteRequest request, CancellationToken cancellationToken)
    {
        IQueryable<Package> search = _context.Packages;

        search = ApplySearchQuery(search, request.Query);
        search = ApplySearchFilters(
            search,
            request.IncludePrerelease,
            request.IncludeSemVer2,
            request.PackageType,
            frameworks: null);

        IReadOnlyList<string> allowedPackageIds;
        if (_policy.IsFilteringEnabled)
        {
            allowedPackageIds = await GetAllowedPackageIdsAsync(
                search.OrderByDescending(p => p.Downloads).Select(p => p.Id).Distinct(),
                search,
                request.Skip,
                request.Take,
                cancellationToken);
        }
        else
        {
            allowedPackageIds = await search
                .OrderByDescending(p => p.Downloads)
                .Select(p => p.Id)
                .Distinct()
                .Skip(request.Skip)
                .Take(request.Take)
                .ToListAsync(cancellationToken);
        }

        return _searchBuilder.BuildAutocomplete(allowedPackageIds);
    }

    public async Task<AutocompleteResponse> ListPackageVersionsAsync(VersionsRequest request, CancellationToken cancellationToken)
    {
        var packageId = request.PackageId.ToLower();
        var search = _context
            .Packages
            .Where(p => p.Id.ToLower().Equals(packageId));

        search = ApplySearchFilters(
            search,
            request.IncludePrerelease,
            request.IncludeSemVer2,
            packageType: null,
            frameworks: null);

        IReadOnlyList<string> packageVersions;
        if (_policy.IsFilteringEnabled)
        {
            packageVersions = (await search.ToListAsync(cancellationToken))
                .Where(IsAllowed)
                .Select(p => p.NormalizedVersionString)
                .ToList();
        }
        else
        {
            packageVersions = await search
                .Select(p => p.NormalizedVersionString)
                .ToListAsync(cancellationToken);
        }

        return _searchBuilder.BuildAutocomplete(packageVersions);
    }

    public async Task<DependentsResponse> FindDependentsAsync(string packageId, CancellationToken cancellationToken)
    {
        var dependents = await _context
            .Packages
            .Where(p => p.Listed)
            .OrderByDescending(p => p.Downloads)
            .Where(p => p.Dependencies.Any(d => d.Id == packageId))
            .Take(20)
            .Select(r => new PackageDependent
            {
                Id = r.Id,
                Description = r.Description,
                TotalDownloads = r.Downloads
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        return _searchBuilder.BuildDependents(dependents);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1862:Use the 'StringComparison' method overloads to perform case-insensitive string comparisons", Justification = "Not for EF queries")]
    private static IQueryable<Package> ApplySearchQuery(IQueryable<Package> query, string search)
    {
        if (string.IsNullOrEmpty(search))
        {
            return query;
        }

        search = search.ToLowerInvariant();

        return query.Where(p => p.Id.ToLower().Contains(search));
    }

    private static IQueryable<Package> ApplySearchFilters(
        IQueryable<Package> query,
        bool includePrerelease,
        bool includeSemVer2,
        string packageType,
        IReadOnlyList<string> frameworks)
    {
        if (!includePrerelease)
        {
            query = query.Where(p => !p.IsPrerelease);
        }

        if (!includeSemVer2)
        {
            query = query.Where(p => p.SemVerLevel != SemVerLevel.SemVer2);
        }

        if (!string.IsNullOrEmpty(packageType))
        {
            query = query.Where(p => p.PackageTypes.Any(t => t.Name == packageType));
        }

        if (frameworks != null)
        {
            query = query.Where(p => p.TargetFrameworks.Any(f => frameworks.Contains(f.Moniker)));
        }

        return query.Where(p => p.Listed);
    }

    private IReadOnlyList<string> GetCompatibleFrameworksOrNull(string framework)
    {
        if (framework == null) return null;

        return _frameworks.FindAllCompatibleFrameworks(framework);
    }

    private async Task<List<string>> GetAllowedPackageIdsAsync(
        IQueryable<string> packageIds,
        IQueryable<Package> packages,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        if (take <= 0)
        {
            return results;
        }

        var offset = 0;
        var remainingSkip = skip;
        var batchSize = Math.Max(take, 20);

        while (results.Count < take)
        {
            var batch = await packageIds
                .Skip(offset)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            offset += batch.Count;

            var allowedIds = (await packages
                    .Where(package => batch.Contains(package.Id))
                    .ToListAsync(cancellationToken))
                .Where(IsAllowed)
                .Select(package => package.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var packageId in batch)
            {
                if (!allowedIds.Contains(packageId))
                {
                    continue;
                }

                if (remainingSkip > 0)
                {
                    remainingSkip--;
                    continue;
                }

                results.Add(packageId);
                if (results.Count == take)
                {
                    break;
                }
            }

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return results;
    }

    private bool IsAllowed(Package package)
    {
        var scope = string.IsNullOrEmpty(package.CachedFrom)
            ? PackageFilterScope.Local
            : PackageFilterScope.CachedUpstream;

        return !_policy.Evaluate(
            new PackageFilterContext(package.Id, package.Version, scope)).IsBlocked;
    }
}
