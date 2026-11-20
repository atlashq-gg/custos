using System.Net.Http.Headers;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>A map data file and the request body of an upload.</summary>
internal static class UploadRequests
{
    public const string UploadPath = "/api/v1/uploads";

    /// <summary>One storage facility with a public stockpile and the reserve stockpile
    /// <c>ATLAS REC</c>.</summary>
    public static readonly byte[] SaveFile = MapDataWriter.File(
        new Pinned(
            "OriginHex",
            0.25f,
            0.75f,
            "StorageFacility",
            FakeWarApi.War142.StartedAt!.Value.AddDays(1),
            [("ItemCrates", "RifleC", 20)],
            [("ATLAS REC", [("Vehicles", "TruckC", 2)])]
        )
    );

    public static ByteArrayContent Body(byte[] bytes, string mediaType = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }
}
