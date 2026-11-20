using Microsoft.EntityFrameworkCore;

namespace Custos.Server.Storage;

/// <summary>
/// Stores the place names and the map items of each region for each war. The static data of a map
/// never changes during the life of the map, so the server asks for it one time for each war, with
/// <c>If-None-Match</c> from the earlier war. The war monitor calls <see cref="EnsureAsync"/> on
/// each interval, so a region that did not answer gets a new request.
/// </summary>
internal sealed class WarMaps(CustosDbContext db, WarApiClient warApi, TimeProvider time)
{
    public async Task EnsureAsync(Guid warId, CancellationToken cancellationToken)
    {
        var done = await db
            .MapRegions.Where(r => r.WarId == warId)
            .Select(r => r.Region)
            .ToListAsync(cancellationToken);
        var names = await warApi.GetMapNamesAsync(cancellationToken);
        if (names is null)
            return;

        foreach (var region in names.Except(done))
        {
            var previous = await db
                .MapRegions.AsNoTracking()
                .Where(r => r.Region == region && r.WarId != warId)
                .OrderByDescending(r => r.FetchedAt)
                .FirstOrDefaultAsync(cancellationToken);
            var map = await warApi.GetStaticMapAsync(
                region,
                previous?.StaticEtag,
                cancellationToken
            );
            var items = map is null
                ? null
                : await warApi.GetMapItemsAsync(region, cancellationToken);
            if (map is null || items is null)
                continue;

            var places = map.NotModified
                ? await db
                    .MapPlaces.AsNoTracking()
                    .Where(p => p.WarId == previous!.WarId && p.Region == region)
                    .OrderBy(p => p.PlaceIndex)
                    .Select(p => new MapText(p.Name, p.X, p.Y, p.Major))
                    .ToListAsync(cancellationToken)
                : map.Places!;

            db.MapRegions.Add(
                new MapRegion
                {
                    WarId = warId,
                    Region = region,
                    StaticEtag = map.ETag,
                    FetchedAt = time.GetUtcNow(),
                }
            );
            db.MapPlaces.AddRange(
                places.Select(
                    (p, i) =>
                        new MapPlace
                        {
                            WarId = warId,
                            Region = region,
                            PlaceIndex = i,
                            Name = p.Text,
                            X = p.X,
                            Y = p.Y,
                            Major = p.Major,
                        }
                )
            );
            db.MapItems.AddRange(
                items.Select(
                    (item, i) =>
                        new MapItem
                        {
                            WarId = warId,
                            Region = region,
                            ItemIndex = i,
                            IconType = item.IconType,
                            X = item.X,
                            Y = item.Y,
                        }
                )
            );
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }
}
