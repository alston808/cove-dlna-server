using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Cove.Sdk;
using Cove.Plugins;
using Cove.Core.Entities;
using System.Xml.Linq;

namespace Cove.DlnaServer;

public sealed class DlnaExtension : CoveExtensionBase, IBackgroundExtension, IApiExtension
{
    private const string Uuid = "b3c7b653-7313-41bb-98f5-9afb762ef4f1"; // Fixed UUID for SSDP
    private static string _currentToken = Guid.NewGuid().ToString("N");
    private static System.Timers.Timer _tokenRotationTimer;

    public DlnaExtension()
    {
        // Rotate the token every 3 hours
        _tokenRotationTimer = new System.Timers.Timer(TimeSpan.FromHours(3).TotalMilliseconds);
        _tokenRotationTimer.Elapsed += (s, e) => { _currentToken = Guid.NewGuid().ToString("N"); };
        _tokenRotationTimer.Start();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ext/com.example.dlna-server/description", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/xml";
            string xml = $@"<?xml version=""1.0""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"">
  <specVersion><major>1</major><minor>0</minor></specVersion>
  <device>
    <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
    <friendlyName>Cove Media Server</friendlyName>
    <manufacturer>alston808</manufacturer>
    <modelName>Windows Media Connect compatible (Cove)</modelName>
    <UDN>uuid:{Uuid}</UDN>
    <dlna:X_DLNADOC xmlns:dlna=""urn:schemas-dlna-org:device-1-0"">DMS-1.50</dlna:X_DLNADOC>
    <serviceList>
      <service>
        <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
        <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>
        <SCPDURL>/api/ext/com.example.dlna-server/content-directory</SCPDURL>
        <controlURL>/api/ext/com.example.dlna-server/control</controlURL>
        <eventSubURL>/api/ext/com.example.dlna-server/events</eventSubURL>
      </service>
      <service>
        <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
        <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
        <SCPDURL>/api/ext/com.example.dlna-server/connection-manager</SCPDURL>
        <controlURL>/api/ext/com.example.dlna-server/control</controlURL>
        <eventSubURL>/api/ext/com.example.dlna-server/events</eventSubURL>
      </service>
      <service>
        <serviceType>urn:microsoft.com:service:X_MS_MediaReceiverRegistrar:1</serviceType>
        <serviceId>urn:microsoft.com:serviceId:X_MS_MediaReceiverRegistrar</serviceId>
        <SCPDURL>/api/ext/com.example.dlna-server/ms-registrar</SCPDURL>
        <controlURL>/api/ext/com.example.dlna-server/control</controlURL>
        <eventSubURL>/api/ext/com.example.dlna-server/events</eventSubURL>
      </service>
    </serviceList>
  </device>
</root>";
            await context.Response.WriteAsync(xml);
        });

        endpoints.MapGet("/api/ext/com.example.dlna-server/connection-manager", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/xml";
            await context.Response.WriteAsync(@"<?xml version=""1.0""?>
<scpd xmlns=""urn:schemas-upnp-org:service-1-0"">
  <specVersion><major>1</major><minor>0</minor></specVersion>
  <actionList>
    <action><name>GetProtocolInfo</name><argumentList><argument><name>Source</name><direction>out</direction><relatedStateVariable>SourceProtocolInfo</relatedStateVariable></argument><argument><name>Sink</name><direction>out</direction><relatedStateVariable>SinkProtocolInfo</relatedStateVariable></argument></argumentList></action>
    <action><name>GetCurrentConnectionIDs</name><argumentList><argument><name>ConnectionIDs</name><direction>out</direction><relatedStateVariable>CurrentConnectionIDs</relatedStateVariable></argument></argumentList></action>
    <action><name>GetCurrentConnectionInfo</name><argumentList><argument><name>ConnectionID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ConnectionID</relatedStateVariable></argument><argument><name>RcsID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_RcsID</relatedStateVariable></argument><argument><name>AVTransportID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_AVTransportID</relatedStateVariable></argument><argument><name>ProtocolInfo</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ProtocolInfo</relatedStateVariable></argument><argument><name>PeerConnectionManager</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionManager</relatedStateVariable></argument><argument><name>PeerConnectionID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionID</relatedStateVariable></argument><argument><name>Direction</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Direction</relatedStateVariable></argument><argument><name>Status</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionStatus</relatedStateVariable></argument></argumentList></action>
  </actionList>
  <serviceStateTable>
    <stateVariable sendEvents=""yes""><name>SourceProtocolInfo</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""yes""><name>SinkProtocolInfo</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""yes""><name>CurrentConnectionIDs</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_ConnectionStatus</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_ConnectionManager</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_Direction</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_ProtocolInfo</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_ConnectionID</name><dataType>i4</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_AVTransportID</name><dataType>i4</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_RcsID</name><dataType>i4</dataType></stateVariable>
  </serviceStateTable>
</scpd>");
        });

        endpoints.MapGet("/api/ext/com.example.dlna-server/ms-registrar", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/xml";
            await context.Response.WriteAsync(@"<?xml version=""1.0""?>
<scpd xmlns=""urn:schemas-upnp-org:service-1-0"">
  <specVersion><major>1</major><minor>0</minor></specVersion>
  <actionList>
    <action><name>IsAuthorized</name><argumentList><argument><name>DeviceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_DeviceID</relatedStateVariable></argument><argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument></argumentList></action>
    <action><name>IsValidated</name><argumentList><argument><name>DeviceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_DeviceID</relatedStateVariable></argument><argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument></argumentList></action>
    <action><name>RegisterDevice</name><argumentList><argument><name>RegistrationReqMsg</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_RegistrationReqMsg</relatedStateVariable></argument><argument><name>RegistrationRespMsg</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_RegistrationRespMsg</relatedStateVariable></argument></argumentList></action>
  </actionList>
  <serviceStateTable>
    <stateVariable sendEvents=""yes""><name>AuthorizationGrantedUpdateID</name><dataType>ui4</dataType></stateVariable>
    <stateVariable sendEvents=""yes""><name>AuthorizationDeniedUpdateID</name><dataType>ui4</dataType></stateVariable>
    <stateVariable sendEvents=""yes""><name>ValidationSucceededUpdateID</name><dataType>ui4</dataType></stateVariable>
    <stateVariable sendEvents=""yes""><name>ValidationRevokedUpdateID</name><dataType>ui4</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_DeviceID</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_Result</name><dataType>int</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_RegistrationReqMsg</name><dataType>bin.base64</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_RegistrationRespMsg</name><dataType>bin.base64</dataType></stateVariable>
  </serviceStateTable>
</scpd>");
        });

        // Protected media streaming endpoint
        endpoints.MapGet("/api/ext/com.example.dlna-server/stream/{id:int}", async (HttpContext context, int id, DbContext db) =>
        {
            var token = context.Request.Query["t"];
            if (token != _currentToken)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Forbidden: Invalid or expired DLNA token.");
                return;
            }

            var video = await db.Set<Video>().Include(v => v.Files).FirstOrDefaultAsync(v => v.Id == id);
            if (video == null || video.Files == null || !video.Files.Any())
            {
                context.Response.StatusCode = 404;
                return;
            }
            
            var path = video.Files.First().Path;
            await context.Response.SendFileAsync(path);
        });

        endpoints.MapGet("/api/ext/com.example.dlna-server/content-directory", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/xml";
            string scpd = @"<?xml version=""1.0""?>
<scpd xmlns=""urn:schemas-upnp-org:service-1-0"">
  <specVersion>
    <major>1</major>
    <minor>0</minor>
  </specVersion>
  <actionList>
    <action>
      <name>Browse</name>
      <argumentList>
        <argument><name>ObjectID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable></argument>
        <argument><name>BrowseFlag</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_BrowseFlag</relatedStateVariable></argument>
        <argument><name>Filter</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable></argument>
        <argument><name>StartingIndex</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable></argument>
        <argument><name>RequestedCount</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
        <argument><name>SortCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable></argument>
        <argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument>
        <argument><name>NumberReturned</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
        <argument><name>TotalMatches</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
        <argument><name>UpdateID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_UpdateID</relatedStateVariable></argument>
      </argumentList>
    </action>
  </actionList>
  <serviceStateTable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_ObjectID</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_Result</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_BrowseFlag</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_Filter</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_SortCriteria</name><dataType>string</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_Index</name><dataType>ui4</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_Count</name><dataType>ui4</dataType></stateVariable>
    <stateVariable sendEvents=""no""><name>A_ARG_TYPE_UpdateID</name><dataType>ui4</dataType></stateVariable>
  </serviceStateTable>
</scpd>";
            await context.Response.WriteAsync(scpd);
        });

        endpoints.MapPost("/api/ext/com.example.dlna-server/control", async (HttpContext context, DbContext db) =>
        {
            using var reader = new System.IO.StreamReader(context.Request.Body);
            var requestBody = await reader.ReadToEndAsync();
            Console.WriteLine("DLNA BROWSE REQUEST:\n" + requestBody);
            
            // Basic string extraction to avoid heavy XML parsing errors
            string objectId = "0";
            string browseFlag = "BrowseDirectChildren";
            
            if (requestBody.Contains("<ObjectID>"))
            {
                var start = requestBody.IndexOf("<ObjectID>") + 10;
                var end = requestBody.IndexOf("</ObjectID>");
                if (end > start) objectId = requestBody.Substring(start, end - start);
            }
            if (requestBody.Contains("<BrowseFlag>"))
            {
                var start = requestBody.IndexOf("<BrowseFlag>") + 12;
                var end = requestBody.IndexOf("</BrowseFlag>");
                if (end > start) browseFlag = requestBody.Substring(start, end - start);
            }

            string didlStr = "";
            int count = 0;
            int totalMatches = 0;

            var hostIp = context.Request.Host.Host;
            var hostPort = context.Request.Host.Port ?? 5073;

            if (browseFlag == "BrowseMetadata")
            {
                didlStr = $@"<container id=""{objectId}"" parentID=""-1"" restricted=""1""><dc:title>Folder</dc:title><upnp:class>object.container</upnp:class></container>";
                count = 1;
                totalMatches = 1;
            }
            else if (browseFlag == "BrowseDirectChildren")
            {
                if (objectId == "0")
                {
                    // Root directory shows "All Videos" folder
                    didlStr = $@"<container id=""1"" parentID=""0"" restricted=""1""><dc:title>All Videos</dc:title><upnp:class>object.container</upnp:class></container>";
                    count = 1;
                    totalMatches = 1;
                }
                else
                {
                    var videos = await db.Set<Video>().Include(v => v.Files).Take(50).ToListAsync();
                    totalMatches = videos.Count;
                    count = videos.Count;

                    var sb = new System.Text.StringBuilder();
                    foreach(var v in videos)
                    {
                        var title = System.Security.SecurityElement.Escape(v.Title ?? "Unknown Video");
                        var mimeType = "video/mp4";
                        var url = $"http://{hostIp}:{hostPort}/api/ext/com.example.dlna-server/stream/{v.Id}?t={_currentToken}";
                        var eUrl = System.Security.SecurityElement.Escape(url);
                        
                        sb.Append($@"<item id=""vid_{v.Id}"" parentID=""{objectId}"" restricted=""1"">");
                        sb.Append($@"<dc:title>{title}</dc:title>");
                        sb.Append($@"<upnp:class>object.item.videoItem</upnp:class>");
                        sb.Append($@"<res protocolInfo=""http-get:*:{mimeType}:DLNA.ORG_PN=AVC_MP4_HP_HD_AAC;DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000"" size=""12345"">{eUrl}</res>");
                        sb.Append($@"</item>");
                    }
                    didlStr = sb.ToString();
                }
            }

            var escapedDidl = System.Security.SecurityElement.Escape($@"<DIDL-Lite xmlns=""urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"">{didlStr}</DIDL-Lite>");

            string responseXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body>
    <u:BrowseResponse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
      <Result>{escapedDidl}</Result>
      <NumberReturned>{count}</NumberReturned>
      <TotalMatches>{totalMatches}</TotalMatches>
      <UpdateID>1</UpdateID>
    </u:BrowseResponse>
  </s:Body>
</s:Envelope>";
            context.Response.ContentType = "text/xml; charset=\"utf-8\"";
            await context.Response.WriteAsync(responseXml);
        });
    }

    
    public async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var logger = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DlnaExtension>>(services);
        logger.LogInformation("DLNA Server starting up... (Custom SSDP)");

        try
        {
            var localIp = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork 
                         && !System.Net.IPAddress.IsLoopback(a.Address)
                         && !a.Address.ToString().StartsWith("172."))
                .Select(a => a.Address.ToString())
                .FirstOrDefault() ?? "127.0.0.1";
                
            var covePort = 5073;

            string location = $"http://{localIp}:{covePort}/api/ext/com.example.dlna-server/description";
            string serverHeader = "Linux/5.15.0 DLNADOC/1.50 UPnP/1.0 MiniDLNA/1.3.3";
            string usnRoot = $"uuid:{Uuid}::upnp:rootdevice";
            string usnDevice = $"uuid:{Uuid}::urn:schemas-upnp-org:device:MediaServer:1";
            string usnUuid = $"uuid:{Uuid}";

            var multicastEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("239.255.255.250"), 1900);
            
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 1900));
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.IP, System.Net.Sockets.SocketOptionName.AddMembership, new System.Net.Sockets.MulticastOption(multicastEndpoint.Address, System.Net.IPAddress.Any));
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.IP, System.Net.Sockets.SocketOptionName.MulticastTimeToLive, 4);

            logger.LogInformation("UPnP Device Published! Waiting for SSDP discovery requests.");

            var buffer = new byte[65536];
            System.Net.EndPoint remoteEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var tcs = new TaskCompletionSource<int>();
                        socket.BeginReceiveFrom(buffer, 0, buffer.Length, System.Net.Sockets.SocketFlags.None, ref remoteEndpoint, ar =>
                        {
                            try
                            {
                                int bytesRead = socket.EndReceiveFrom(ar, ref remoteEndpoint);
                                string message = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                                
                                if (message.Contains("M-SEARCH"))
                                {
                                    string[] lines = message.Split('\n');
                                    string st = lines.FirstOrDefault(l => l.StartsWith("ST: ", StringComparison.OrdinalIgnoreCase))?.Substring(4).Trim();
                                    
                                    if (st != null && (st.Contains("ssdp:all") || st.Contains("upnp:rootdevice") || st.Contains("MediaServer:1") || st.Contains(Uuid)))
                                    {
                                        var targets = new[] {
                                            ("upnp:rootdevice", usnRoot),
                                            (usnUuid, usnUuid),
                                            ("urn:schemas-upnp-org:device:MediaServer:1", usnDevice)
                                        };

                                        foreach (var target in targets)
                                        {
                                            string response = $"HTTP/1.1 200 OK\r\n" +
                                                              $"CACHE-CONTROL: max-age=1800\r\n" +
                                                              $"DATE: {DateTime.UtcNow.ToString("r")}\r\n" +
                                                              $"EXT:\r\n" +
                                                              $"LOCATION: {location}\r\n" +
                                                              $"SERVER: {serverHeader}\r\n" +
                                                              $"ST: {target.Item1}\r\n" +
                                                              $"USN: {target.Item2}\r\n\r\n";

                                            var responseBytes = System.Text.Encoding.UTF8.GetBytes(response);
                                            socket.SendTo(responseBytes, remoteEndpoint);
                                        }
                                    }
                                }
                            }
                            catch { }
                            tcs.TrySetResult(0);
                        }, null);
                        
                        using (ct.Register(() => tcs.TrySetCanceled()))
                        {
                            await tcs.Task;
                        }
                    }
                    catch { break; }
                }
            });

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var targets = new[] {
                            ("upnp:rootdevice", usnRoot),
                            (usnUuid, usnUuid),
                            ("urn:schemas-upnp-org:device:MediaServer:1", usnDevice)
                        };

                        foreach (var target in targets)
                        {
                            string notify = $"NOTIFY * HTTP/1.1\r\n" +
                                            $"HOST: 239.255.255.250:1900\r\n" +
                                            $"CACHE-CONTROL: max-age=1800\r\n" +
                                            $"LOCATION: {location}\r\n" +
                                            $"NT: {target.Item1}\r\n" +
                                            $"NTS: ssdp:alive\r\n" +
                                            $"SERVER: {serverHeader}\r\n" +
                                            $"USN: {target.Item2}\r\n\r\n";

                            var notifyBytes = System.Text.Encoding.UTF8.GetBytes(notify);
                            socket.SendTo(notifyBytes, multicastEndpoint);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(60), ct);
                    }
                    catch { break; }
                }
            });

            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (TaskCanceledException)
        {
            logger.LogInformation("DLNA Server shutting down gracefully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DLNA Server encountered a fatal error.");
        }
    }

}
