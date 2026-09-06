using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dlna.ContentDirectory;

/// <summary>
/// Provides a narrowly scoped fallback when the server's top-parent movie query
/// misses items that are still descendants of the configured media folders.
/// </summary>
internal sealed class MovieLibraryQueryScope
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly Dictionary<(Guid LibraryId, Guid UserId), Guid[]?> _scopes = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieLibraryQueryScope"/> class.
    /// </summary>
    /// <param name="libraryManager">The server library manager.</param>
    /// <param name="logger">The request logger.</param>
    public MovieLibraryQueryScope(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Clears request-local results so scans and permission changes are not cached.
    /// </summary>
    public void Reset() => _scopes.Clear();

    /// <summary>
    /// Identifies a movie library or its movie user view, not an arbitrary folder.
    /// </summary>
    /// <param name="item">The requested library.</param>
    /// <returns>Whether the item is a movie library view.</returns>
    public static bool IsMovieLibrary(BaseItem item) =>
        item is IHasCollectionType { CollectionType: CollectionType.movies };

    /// <summary>
    /// Replaces only a demonstrated incomplete native movie scope. All other
    /// filters, including the user, ratings, tags, favorites, and paging, remain.
    /// </summary>
    /// <param name="parent">The original library or user view.</param>
    /// <param name="query">The query to scope.</param>
    /// <returns>Whether the physical-ancestor fallback was applied.</returns>
    public bool TryApply(BaseItem parent, InternalItemsQuery query)
    {
        var user = query.User;
        if (user is null || !IsMovieLibrary(parent))
        {
            return false;
        }

        var key = (parent.Id, user.Id);
        if (!_scopes.TryGetValue(key, out var ancestors))
        {
            ancestors = SelectFallback(parent, user);
            _scopes.Add(key, ancestors);
        }

        if (ancestors is null || ancestors.Length == 0)
        {
            return false;
        }

        // Parent would be translated back to TopParentIds by GetItemsResult.
        // Keep real folder ancestry instead; never substitute an empty filter.
        query.Parent = null;
        query.TopParentIds = [];
        query.AncestorIds = ancestors;
        query.Recursive = true;
        return true;
    }

    private Guid[]? SelectFallback(BaseItem parent, User user)
    {
        var nativeQuery = CreateCountQuery(user);
        nativeQuery.Parent = parent;
        var nativeCount = _libraryManager.GetItemsResult(nativeQuery).TotalRecordCount;

        var ancestors = ResolvePhysicalAncestors(parent, user);
        if (ancestors.Length == 0)
        {
            _logger.LogInformation(
                "DLNA movie-scope v2: library={LibraryId}, user={UserId}, nativeMovies={NativeCount}, physicalFolders=0; retaining native scope (no safe physical fallback).",
                parent.Id,
                user.Id,
                nativeCount
            );
            return null;
        }

        var physicalQuery = CreateCountQuery(user);
        physicalQuery.AncestorIds = ancestors;
        var physicalCount = _libraryManager.GetItemsResult(physicalQuery).TotalRecordCount;

        // A normal library stays on its existing query path. Switch only when a
        // permission-preserving query of the SAME library finds missing movies.
        var useFallback = physicalCount > nativeCount;
        _logger.LogInformation(
            "DLNA movie-scope v2: library={LibraryId}, user={UserId}, nativeMovies={NativeCount}, physicalMovies={PhysicalCount}, physicalFolders={FolderCount}, mode={Mode}.",
            parent.Id,
            user.Id,
            nativeCount,
            physicalCount,
            ancestors.Length,
            useFallback ? "physical-ancestors" : "native"
        );
        return useFallback ? ancestors : null;
    }

    private static InternalItemsQuery CreateCountQuery(User user) => new(user)
    {
        Recursive = true,
        IncludeItemTypes = [BaseItemKind.Movie],
        IsVirtualItem = false,
        Limit = 0,
        EnableTotalRecordCount = true,
        DtoOptions = new DtoOptions(false),
    };

    private Guid[] ResolvePhysicalAncestors(BaseItem parent, User user)
    {
        // Use the user's visible libraries, not the unrestricted server root.
        var visibleLibraries = _libraryManager.GetUserRootFolder()
            .GetChildren(user, true)
            .OfType<CollectionFolder>()
            .ToArray();
        var libraries = ResolveLibraries(parent, user, visibleLibraries);
        var ids = new HashSet<Guid>();

        foreach (var library in libraries)
        {
            var paths = library.GetLibraryOptions().PathInfos
                .Select(info => info.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (paths.Length == 0)
            {
                paths = library.PhysicalLocations;
            }

            if (paths.Length == 0)
            {
                return [];
            }

            // Cached IDs can lag behind a path/library change. Prefer resolving
            // the configured paths from the database, without rescanning files.
            var cachedFolders = library.GetPhysicalFolders().ToArray();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var folder = _libraryManager.FindByPath(path, true) as Folder;
                if (folder is null)
                {
                    folder = cachedFolders.FirstOrDefault(candidate => SamePath(candidate.Path, path));
                }

                // Virtual libraries must never reach the genre query: the server
                // would translate those back into the failing TopParentIds filter.
                if (folder is not null
                    && folder is not ICollectionFolder
                    && folder is not UserView
                    && !folder.IsPhysicalRoot
                    && folder.Id != Guid.Empty
                    && SamePath(folder.Path, path))
                {
                    ids.Add(folder.Id);
                }
                else
                {
                    // Do not switch a multi-path/grouped library to a partial
                    // scope just because only some of its folders resolved.
                    _logger.LogWarning(
                        "DLNA movie-scope v2: library={LibraryId} has an unresolved configured media folder; retaining native scope.",
                        library.Id
                    );
                    return [];
                }
            }
        }

        return ids.ToArray();
    }

    private CollectionFolder[] ResolveLibraries(
        BaseItem parent,
        User user,
        CollectionFolder[] visibleLibraries
    )
    {
        var visited = new HashSet<Guid>();
        var current = parent;
        while (visited.Add(current.Id))
        {
            if (current is CollectionFolder)
            {
                return visibleLibraries.Where(library => library.Id == current.Id).ToArray();
            }

            if (current is not UserView view)
            {
                return [];
            }

            var sourceId = view.DisplayParentId != Guid.Empty ? view.DisplayParentId : view.ParentId;
            if (sourceId != Guid.Empty)
            {
                var source = _libraryManager.GetItemById(sourceId);
                if (source is null)
                {
                    return [];
                }

                current = source;
                continue;
            }

            if (view.CollectionType != CollectionType.movies)
            {
                return [];
            }

            // A grouped movie view includes only explicitly grouped, visible
            // movie/mixed libraries. Never fall back to every movie on the server.
            var groupedIds = user.GetPreferenceValues<Guid>(PreferenceKind.GroupedFolders);
            return visibleLibraries
                .Where(library => library.CollectionType is null or CollectionType.movies)
                .Where(library => groupedIds.Contains(library.Id))
                .ToArray();
        }

        return [];
    }

    private static bool SamePath(string? left, string right) =>
        left is not null && string.Equals(
            left.TrimEnd('/', '\\'),
            right.TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );
}
