using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Cove.Core.Entities;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cove.DlnaServer;

public sealed class DlnaExtension : CoveExtensionBase, IApiExtension, IBackgroundExtension
{
    private const string DeviceUuid = "d4d8c764-8424-42cc-a9f6-0b0c7a3f5b02";
    private const string DeviceType = "urn:schemas-upnp-org:device:MediaServer:1";
    private const string ContentDirectoryType = "urn:schemas-upnp-org:service:ContentDirectory:1";
    private const string ConnectionManagerType = "urn:schemas-upnp-org:service:ConnectionManager:1";
    private const string SoapEnvelope = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string DidlNamespace = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";
    private const string UpnpNamespace = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private const string SsdpAddress = "239.255.255.250";
    private const int SsdpPort = 1900;

    private static readonly XNamespace Soap = SoapEnvelope;
    private static readonly XNamespace Didl = DidlNamespace;
    private static readonly XNamespace Dc = DcNamespace;
    private static readonly XNamespace Upnp = UpnpNamespace;
    private static readonly XNamespace Dlna = "urn:schemas-dlna-org:device-1-0";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/description.xml", (HttpContext context) =>
            Xml(context, DeviceDescription(context)));
        endpoints.MapGet("/cds_scpd.xml", (HttpContext context) =>
            Xml(context, ContentDirectoryScpd()));
        endpoints.MapGet("/cm_scpd.xml", (HttpContext context) =>
            Xml(context, ConnectionManagerScpd()));
        endpoints.MapPost("/cds/control", (HttpContext context, DbContext db) =>
            HandleContentDirectoryAsync(context, db, context.RequestAborted));
        endpoints.MapPost("/cm/control", (HttpContext context) =>
            HandleConnectionManagerAsync(context, context.RequestAborted));
        endpoints.MapGet("/media/{id:int}", StreamMediaAsync);
    }

    public async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var logger = services.GetRequiredService<ILogger<DlnaExtension>>();
        var port = GetHttpPort();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(SsdpAddress), IPAddress.Any));

            var interfaces = GetLocalAddresses().ToArray();
            logger.LogInformation("DLNA SSDP listener started on {Port} for {InterfaceCount} network interfaces", SsdpPort, interfaces.Length);
            await Task.WhenAll(
                ReceiveSearchesAsync(socket, port, logger, ct),
                AnnounceAsync(socket, port, logger, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("DLNA SSDP listener stopped.");
        }
        catch (SocketException ex)
        {
            logger.LogError(ex, "Unable to bind the DLNA SSDP socket on UDP {Port}.", SsdpPort);
        }
        finally
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.DropMembership,
                    new MulticastOption(IPAddress.Parse(SsdpAddress), IPAddress.Any));
            }
            catch (SocketException) { }
        }
    }

    private static async Task ReceiveSearchesAsync(Socket socket, int port, ILogger logger, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                var message = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                if (!message.StartsWith("M-SEARCH ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var headers = ParseHeaders(message);
                if (!headers.TryGetValue("st", out var searchTarget) || !MatchesSearchTarget(searchTarget))
                    continue;

                var location = $"http://{GetAdvertisedAddress()}:{port}/description.xml";
                foreach (var target in SsdpTargets())
                {
                    if (!searchTarget.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase) &&
                        !searchTarget.Equals(target.St, StringComparison.OrdinalIgnoreCase))
                        continue;
                    await SendSsdpResponseAsync(socket, result.RemoteEndPoint, target.St, target.Usn, location, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "SSDP receive failed; retrying.");
            }
        }
    }

    private static async Task AnnounceAsync(Socket socket, int port, ILogger logger, CancellationToken ct)
    {
        var destination = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var address in GetLocalAddresses())
                foreach (var target in SsdpTargets())
                {
                    var location = $"http://{address}:{port}/description.xml";
                    var notify = $"NOTIFY * HTTP/1.1\r\nHOST: {SsdpAddress}:{SsdpPort}\r\n" +
                        $"CACHE-CONTROL: max-age=1800\r\nLOCATION: {location}\r\nNT: {target.St}\r\n" +
                        $"NTS: ssdp:alive\r\nSERVER: Cove/1.0 UPnP/1.1 DLNADOC/1.50\r\nUSN: {target.Usn}\r\n\r\n";
                    await socket.SendToAsync(Encoding.UTF8.GetBytes(notify), SocketFlags.None, destination, ct);
                }
                await Task.Delay(TimeSpan.FromMinutes(15), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (SocketException ex) { logger.LogWarning(ex, "SSDP announcement failed; retrying."); }
        }
    }

    private static async Task<IResult> StreamMediaAsync(HttpContext context, int id, DbContext db)
    {
        var video = await db.Set<Video>().Include(v => v.Files).AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == id, context.RequestAborted);
        var file = video?.Files?.FirstOrDefault();
        if (file is null || string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path))
            return Results.NotFound();

        var contentType = GetContentType(file.Format, file.Path);
        return Results.Stream(
            File.OpenRead(file.Path),
            contentType,
            fileDownloadName: file.Basename,
            enableRangeProcessing: true);
    }

    private static async Task HandleContentDirectoryAsync(HttpContext context, DbContext db, CancellationToken ct)
    {
        XDocument request;
        try
        {
            request = await XDocument.LoadAsync(context.Request.Body, LoadOptions.None, ct);
        }
        catch (XmlException)
        {
            await SoapFaultAsync(context, 402, "Invalid Args");
            return;
        }

        var action = request.Descendants().FirstOrDefault(e => e.Parent?.Name.LocalName == "Body");
        if (action is null)
        {
            await SoapFaultAsync(context, 402, "Invalid Args");
            return;
        }

        try
        {
            switch (action.Name.LocalName)
            {
                case "Browse":
                    await BrowseAsync(context, db, action, ct);
                    break;
                case "GetObjectInfo":
                    await GetObjectInfoAsync(context, db, action, ct);
                    break;
                case "GetSearchCapabilities":
                    await SoapResponseAsync(context, "GetSearchCapabilities", ContentDirectoryType,
                        new Dictionary<string, string> { ["SearchCaps"] = "dc:title,upnp:class" });
                    break;
                case "GetSortCapabilities":
                    await SoapResponseAsync(context, "GetSortCapabilities", ContentDirectoryType,
                        new Dictionary<string, string> { ["SortCaps"] = "dc:title,dc:date" });
                    break;
                case "GetContainerUpdateStatus":
                    await SoapResponseAsync(context, "GetContainerUpdateStatus", ContentDirectoryType,
                        new Dictionary<string, string> { ["ContainerUpdateID"] = "1" });
                    break;
                default:
                    await SoapFaultAsync(context, 401, "Invalid Action");
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            await SoapFaultAsync(context, 501, "Action Failed");
        }
    }

    private static async Task HandleConnectionManagerAsync(HttpContext context, CancellationToken ct)
    {
        XDocument request;
        try { request = await XDocument.LoadAsync(context.Request.Body, LoadOptions.None, ct); }
        catch (XmlException)
        {
            await SoapFaultAsync(context, 402, "Invalid Args");
            return;
        }

        var action = request.Descendants().FirstOrDefault(e => e.Parent?.Name.LocalName == "Body");
        if (action?.Name.LocalName != "GetProtocolInfo")
        {
            await SoapFaultAsync(context, action is null ? 402 : 401, action is null ? "Invalid Args" : "Invalid Action");
            return;
        }

        await SoapResponseAsync(context, "GetProtocolInfo", ConnectionManagerType,
            new Dictionary<string, string>
            {
                ["Source"] = "http-get:*:video/mp4:*",
                ["Sink"] = string.Empty
            });
    }

    private static async Task BrowseAsync(HttpContext context, DbContext db, XElement action, CancellationToken ct)
    {
        var objectId = Argument(action, "ObjectID") ?? "0";
        var flag = Argument(action, "BrowseFlag") ?? "BrowseDirectChildren";
        if (flag is not ("BrowseMetadata" or "BrowseDirectChildren"))
        {
            await SoapFaultAsync(context, 712, "No Such Object");
            return;
        }

        var start = ParseNonNegative(Argument(action, "StartingIndex"));
        var requested = ParseNonNegative(Argument(action, "RequestedCount"));
        var videos = objectId == "1"
            ? await db.Set<Video>().Include(v => v.Files).AsNoTracking().OrderBy(v => v.Id).ToListAsync(ct)
            : new List<Video>();

        XElement result;
        var total = 0;
        if (flag == "BrowseMetadata")
        {
            result = objectId switch
            {
                "0" => Container("0", "-1", "Cove", videos.Count),
                "1" => Container("1", "0", "All Videos", videos.Count),
                _ when TryVideoId(objectId, out var id) && videos.FirstOrDefault(v => v.Id == id) is { } video =>
                    VideoItem(context, video),
                _ => throw new InvalidOperationException("No such object")
            };
            total = 1;
        }
        else if (objectId == "0")
        {
            result = new XElement(Didl + "DIDL-Lite", Container("1", "0", "All Videos", videos.Count));
            total = 1;
        }
        else
        {
            total = videos.Count;
            var page = videos.Skip(start).Take(requested == 0 ? int.MaxValue : requested)
                .Select(video => VideoItem(context, video));
            result = new XElement(Didl + "DIDL-Lite", page);
        }

        if (result.Name != Didl + "DIDL-Lite")
            result = new XElement(Didl + "DIDL-Lite", result);
        var items = result.Elements().ToArray();
        await SoapResponseAsync(context, "Browse", ContentDirectoryType, new Dictionary<string, string>
        {
            ["Result"] = result.ToString(SaveOptions.DisableFormatting),
            ["NumberReturned"] = (flag == "BrowseMetadata" || objectId == "0" ? 1 : items.Length).ToString(CultureInfo.InvariantCulture),
            ["TotalMatches"] = total.ToString(CultureInfo.InvariantCulture),
            ["UpdateID"] = "1"
        }, escapeResult: false);
    }

    private static async Task GetObjectInfoAsync(HttpContext context, DbContext db, XElement action, CancellationToken ct)
    {
        var objectId = Argument(action, "ObjectID");
        if (!TryVideoId(objectId, out var id))
        {
            await SoapFaultAsync(context, 701, "No Such Object");
            return;
        }

        var video = await db.Set<Video>().Include(v => v.Files).AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == id, ct);
        if (video is null)
        {
            await SoapFaultAsync(context, 701, "No Such Object");
            return;
        }

        var file = video.Files?.FirstOrDefault();
        await SoapResponseAsync(context, "GetObjectInfo", ContentDirectoryType, new Dictionary<string, string>
        {
            ["Result"] = new XElement(Didl + "DIDL-Lite",
                VideoItem(context, video)).ToString(SaveOptions.DisableFormatting),
            ["MIMEType"] = file is null ? "video/mp4" : GetContentType(file.Format, file.Path),
            ["DLNAProfileID"] = string.Empty,
            ["Size"] = file?.Size.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["ImportUri"] = string.Empty,
            ["Width"] = file?.Width.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["Height"] = file?.Height.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["ColorDepth"] = "0",
            ["Protection"] = string.Empty,
            ["Duration"] = FormatDuration(file?.Duration),
            ["Bitrate"] = file?.BitRate.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["SampleFrequency"] = "0",
            ["BitsPerSample"] = "0",
            ["NrAudioChannels"] = "0",
            ["AlbumArtURI"] = string.Empty,
            ["Artist"] = string.Empty,
            ["Album"] = string.Empty,
            ["Genre"] = string.Empty,
            ["Publisher"] = string.Empty,
            ["OriginalTrackNumber"] = "0",
            ["Date"] = video.Date?.ToString() ?? string.Empty,
            ["LastPlaybackPosition"] = "0",
            ["PlayCount"] = "0",
            ["RecordQuality"] = string.Empty
        }, escapeResult: false);
    }

    private static XElement VideoItem(HttpContext context, Video video)
    {
        var file = video.Files?.FirstOrDefault();
        var item = new XElement(Didl + "item",
            new XAttribute("id", $"v-{video.Id}"),
            new XAttribute("parentID", "1"),
            new XAttribute("restricted", "1"),
            new XElement(Dc + "title", video.Title ?? $"Video {video.Id}"),
            new XElement(Upnp + "class", "object.item.videoItem"));
        if (file is not null)
        {
            var res = new XElement(Didl + "res",
                new XAttribute("protocolInfo", $"http-get:*:{GetContentType(file.Format, file.Path)}:*"),
                new XAttribute("size", file.Size),
                new XAttribute("duration", FormatDuration(file.Duration)),
                new XAttribute("resolution", $"{file.Width}x{file.Height}"),
                MediaUrl(context, video.Id));
            item.Add(res);
        }
        return item;
    }

    private static XElement Container(string id, string parentId, string title, int childCount) =>
        new(Didl + "container",
            new XAttribute("id", id), new XAttribute("parentID", parentId),
            new XAttribute("restricted", "1"), new XAttribute("childCount", childCount),
            new XElement(Dc + "title", title), new XElement(Upnp + "class", "object.container"));

    private static async Task SoapResponseAsync(HttpContext context, string action, string serviceType,
        IReadOnlyDictionary<string, string> values, bool escapeResult = true)
    {
        var body = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XAttribute(Soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(Soap + "Body",
                new XElement(XName.Get($"{action}Response", serviceType),
                    values.Select(pair => new XElement(pair.Key, escapeResult ? pair.Value : ParseXmlValue(pair.Value))))));
        await WriteXmlAsync(context, body);
    }

    private static async Task SoapFaultAsync(HttpContext context, int code, string description)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var body = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XElement(Soap + "Body",
                new XElement(Soap + "Fault",
                    new XElement("faultcode", "s:Client"),
                    new XElement("faultstring", "UPnPError"),
                    new XElement("detail", new XElement(XName.Get("UPnPError", "urn:schemas-upnp-org:control-1-0"),
                        new XElement("errorCode", code), new XElement("errorDescription", description))))));
        await WriteXmlAsync(context, body);
    }

    private static async Task WriteXmlAsync(HttpContext context, XElement body)
    {
        context.Response.ContentType = "text/xml; charset=utf-8";
        await context.Response.WriteAsync(new XDocument(new XDeclaration("1.0", "utf-8", null), body).ToString(), context.RequestAborted);
    }

    private static IResult Xml(HttpContext context, string xml)
    {
        context.Response.ContentType = "text/xml; charset=utf-8";
        return Results.Content(xml, "text/xml", Encoding.UTF8);
    }

    private static string DeviceDescription(HttpContext context)
    {
        var root = new XElement("root", new XAttribute("xmlns", "urn:schemas-upnp-org:device-1-0"),
            new XElement("specVersion", new XElement("major", "1"), new XElement("minor", "0")),
            new XElement("device",
                new XElement("deviceType", DeviceType), new XElement("friendlyName", "Cove Media Server"),
                new XElement("manufacturer", "Cove"), new XElement("modelName", "Cove DLNA Server"),
                new XElement("modelNumber", "1.0"), new XElement("UDN", $"uuid:{DeviceUuid}"),
                new XElement(Dlna + "X_DLNADOC", "DMS-1.50"),
                new XElement("serviceList",
                    Service("ContentDirectory", ContentDirectoryType, "cds_scpd.xml", "cds/control"),
                    Service("ConnectionManager", ConnectionManagerType, "cm_scpd.xml", "cm/control"))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    private static XElement Service(string id, string type, string scpd, string control) =>
        new("service", new XElement("serviceType", type),
            new XElement("serviceId", $"urn:upnp-org:serviceId:{id}"),
            new XElement("SCPDURL", $"/{scpd}"), new XElement("controlURL", $"/{control}"),
            new XElement("eventSubURL", "/events"));

    private static string ContentDirectoryScpd() => Scpd("ContentDirectory",
        ("Browse", "ObjectID,BrowseFlag,Filter,StartingIndex,RequestedCount,SortCriteria,Result,NumberReturned,TotalMatches,UpdateID"),
        ("GetObjectInfo", "ObjectID,Result,MIMEType,DLNAProfileID,Size,ImportUri,Width,Height,ColorDepth,Protection,Duration,Bitrate,SampleFrequency,BitsPerSample,NrAudioChannels,AlbumArtURI,Artist,Album,Genre,Publisher,OriginalTrackNumber,Date,LastPlaybackPosition,PlayCount,RecordQuality"),
        ("GetSearchCapabilities", "SearchCaps"), ("GetSortCapabilities", "SortCaps"),
        ("GetContainerUpdateStatus", "ContainerID,ContainerUpdateID"));

    private static string ConnectionManagerScpd() => Scpd("ConnectionManager",
        ("GetProtocolInfo", "Source,Sink"));

    private static string Scpd(string _, params (string Name, string Arguments)[] actions)
    {
        XNamespace ns = "urn:schemas-upnp-org:service-1-0";
        var actionElements = actions.Select(action => new XElement(ns + "action",
            new XElement(ns + "name", action.Name),
            new XElement(ns + "argumentList", action.Arguments.Split(',').Select(name =>
                new XElement(ns + "argument", new XElement(ns + "name", name),
                    new XElement(ns + "direction", IsOutputArgument(action.Name, name) ? "out" : "in"),
                    new XElement(ns + "relatedStateVariable", $"A_ARG_TYPE_{name}"))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "scpd", new XElement(ns + "specVersion", new XElement(ns + "major", "1"), new XElement(ns + "minor", "0")),
                new XElement(ns + "actionList", actionElements),
                new XElement(ns + "serviceStateTable", new XElement(ns + "stateVariable",
                    new XAttribute("sendEvents", "no"), new XElement(ns + "name", "A_ARG_TYPE_Result"),
                    new XElement(ns + "dataType", "string"))))).ToString();
    }

    private static bool IsOutputArgument(string action, string name) =>
        (action == "GetObjectInfo" && name != "ObjectID") ||
        (action == "GetProtocolInfo") ||
        name is "Result" or "NumberReturned" or "TotalMatches" or "UpdateID" or "SearchCaps" or "SortCaps" or "ContainerUpdateID";

    private static string? Argument(XElement action, string name) =>
        action.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static object ParseXmlValue(string value) =>
        value.StartsWith("<", StringComparison.Ordinal) ? XElement.Parse(value, LoadOptions.PreserveWhitespace) : value;

    private static bool TryVideoId(string? value, out int id)
    {
        var normalized = value?.StartsWith("v-", StringComparison.OrdinalIgnoreCase) == true ? value[2..] : value;
        return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private static int ParseNonNegative(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : 0;

    private static string MediaUrl(HttpContext context, int id) =>
        $"{context.Request.Scheme}://{context.Request.Host}/media/{id}";

    private static string FormatDuration(double? seconds) =>
        seconds is null or < 0 ? "00:00:00" : TimeSpan.FromSeconds(seconds.Value).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static string GetContentType(string? format, string path) =>
        format?.ToLowerInvariant() switch
        {
            "mp4" or "m4v" => "video/mp4",
            "mkv" => "video/x-matroska",
            "webm" => "video/webm",
            "mov" => "video/quicktime",
            _ => Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                _ => "application/octet-stream"
            }
        };

    private static int GetHttpPort() =>
        int.TryParse(Environment.GetEnvironmentVariable("COVE_HTTP_PORT"), out var port) && port is > 0 and <= 65535 ? port : 5073;

    private static IEnumerable<string> GetLocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Select(address => address.ToString())
            .Distinct(StringComparer.Ordinal);

    private static string GetAdvertisedAddress() =>
        GetLocalAddresses().FirstOrDefault() ?? "127.0.0.1";

    private static bool MatchesSearchTarget(string target) =>
        target.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase) ||
        SsdpTargets().Any(item => item.St.Equals(target, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string St, string Usn)> SsdpTargets()
    {
        yield return ("upnp:rootdevice", $"uuid:{DeviceUuid}::upnp:rootdevice");
        yield return (DeviceType, $"uuid:{DeviceUuid}::{DeviceType}");
        yield return ($"uuid:{DeviceUuid}", $"uuid:{DeviceUuid}");
    }

    private static async Task SendSsdpResponseAsync(Socket socket, EndPoint endpoint, string st, string usn, string location, CancellationToken ct)
    {
        var response = $"HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nDATE: {DateTime.UtcNow:R}\r\n" +
            $"EXT:\r\nLOCATION: {location}\r\nSERVER: Cove/1.0 UPnP/1.1 DLNADOC/1.50\r\nST: {st}\r\nUSN: {usn}\r\n\r\n";
        await socket.SendToAsync(Encoding.UTF8.GetBytes(response), SocketFlags.None, endpoint, ct);
    }

    private static Dictionary<string, string> ParseHeaders(string message) =>
        message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim().ToLowerInvariant(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
}
