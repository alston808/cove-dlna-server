using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Cove.Sdk;
using Cove.Plugins;

namespace Cove.DlnaServer;

public sealed class DlnaExtension : CoveExtensionBase, IBackgroundExtension, IApiExtension
{
    private const string Uuid = "b3c7b653-7313-41bb-98f5-9afb762ef4f1"; // Fixed UUID for SSDP

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ext/com.example.dlna-server/description", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/xml";
            string xml = $@"<?xml version=""1.0""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"">
  <specVersion>
    <major>1</major>
    <minor>0</minor>
  </specVersion>
  <device>
    <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
    <friendlyName>Cove Media Server</friendlyName>
    <manufacturer>alston808</manufacturer>
    <modelName>Cove DLNA Extension</modelName>
    <UDN>uuid:{Uuid}</UDN>
    <serviceList>
      <service>
        <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
        <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>
        <SCPDURL>/api/ext/com.example.dlna-server/content-directory</SCPDURL>
        <controlURL>/api/ext/com.example.dlna-server/control</controlURL>
        <eventSubURL>/api/ext/com.example.dlna-server/events</eventSubURL>
      </service>
    </serviceList>
  </device>
</root>";
            await context.Response.WriteAsync(xml);
        });
    }
    public async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var logger = services.GetRequiredService<ILogger<DlnaExtension>>();
        logger.LogInformation("DLNA Server starting up...");

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
                
            var covePort = 5073; // Based on your docker-compose.allinone.yml

            var deviceDefinition = new Rssdp.SsdpRootDevice()
            {
                CacheLifetime = TimeSpan.FromMinutes(30),
                Location = new Uri($"http://{localIp}:{covePort}/api/ext/com.example.dlna-server/description"),
                DeviceTypeNamespace = "schemas-upnp-org",
                DeviceType = "MediaServer",
                DeviceVersion = 1,
                FriendlyName = "Cove Media Server",
                Manufacturer = "alston808",
                ModelName = "Cove DLNA Extension",
                Uuid = Uuid
            };

            // Using default SsdpDevicePublisher binds to all available IP addresses
            using var devicePublisher = new Rssdp.SsdpDevicePublisher();
            
            devicePublisher.AddDevice(deviceDefinition);
            logger.LogInformation("UPnP Device Published! Waiting for SSDP discovery requests.");

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(10000, ct);
                // The devicePublisher automatically responds to M-SEARCH requests in the background.
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("DLNA Server shutting down gracefully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DLNA Server encountered an error.");
        }
    }
}
