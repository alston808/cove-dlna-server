using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cove.Sdk;
using Cove.Plugins;

namespace Cove.DlnaServer;

public sealed class DlnaExtension : CoveExtensionBase, IBackgroundExtension
{
    private Process _nodeProcess;
    private ExtensionContext? _context;

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        _context = context;
        // Store context for background worker; ExtensionContext is available here from the host.
        services.AddSingleton(new DlnaExtensionState { Context = context });
    }

    public override UIManifest GetUIManifest() =>
        ManifestBuilder().Build();

    public async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var logger = services.GetRequiredService<ILogger<DlnaExtension>>();
        // Try to resolve from DI (host may inject it) or fall back to the instance stored during ConfigureServices.
        var context = services.GetService<ExtensionContext>() ?? _context;
        if (context == null)
        {
            logger.LogWarning("ExtensionContext not available; using assembly path for NodeBridge resolution.");
        }

        // Find the NodeBridge folder — try context path first, then search near assembly
        string extensionPath = context?.DataDirectory ?? AppContext.BaseDirectory;
        string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;

        string nodeBridgePath = Path.Combine(extensionPath, "NodeBridge");
        if (!File.Exists(Path.Combine(nodeBridgePath, "index.js")))
        {
            // DataDirectory may be parent; try extension subfolder
            nodeBridgePath = Path.Combine(extensionPath, "cove.dlna-server", "NodeBridge");
        }
        if (!File.Exists(Path.Combine(nodeBridgePath, "index.js")))
        {
            // Search upward from assembly folder for extension directory with NodeBridge
            string searchDir = assemblyDir;
            while (!string.IsNullOrEmpty(searchDir) && searchDir.Length > 3)
            {
                if (Directory.Exists(Path.Combine(searchDir, "NodeBridge")) &&
                    File.Exists(Path.Combine(searchDir, "NodeBridge", "index.js")))
                {
                    nodeBridgePath = Path.Combine(searchDir, "NodeBridge");
                    break;
                }
                // Also check if parent is the extension folder (has extension.json + DLL + NodeBridge)
                string parentDir = Path.GetDirectoryName(searchDir);
                if (!string.IsNullOrEmpty(parentDir) && parentDir.Length > 3)
                {
                    if (File.Exists(Path.Combine(parentDir, "Cove.DlnaServer.dll")) &&
                        Directory.Exists(Path.Combine(parentDir, "NodeBridge")))
                    {
                        nodeBridgePath = Path.Combine(parentDir, "NodeBridge");
                        break;
                    }
                }
                searchDir = parentDir;
            }
        }
        string indexJsPath = Path.Combine(nodeBridgePath, "index.js");

        logger.LogInformation($"Starting Node.js DLNA Bridge from {indexJsPath}...");

        if (!File.Exists(indexJsPath))
        {
            logger.LogError($"Could not find index.js at {indexJsPath}");
            return;
        }

        try
        {
            // Install dependencies if node_modules is missing
            if (!Directory.Exists(Path.Combine(nodeBridgePath, "node_modules")))
            {
                logger.LogInformation("Running npm install for DLNA Bridge...");
                var npmProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "npm",
                        Arguments = "install --production",
                        WorkingDirectory = nodeBridgePath,
                        UseShellExecute = true,
                    }
                };
                npmProcess.Start();
                await npmProcess.WaitForExitAsync(ct);
            }

            // Start the Node.js process
            _nodeProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = "index.js",
                    WorkingDirectory = nodeBridgePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            _nodeProcess.OutputDataReceived += (sender, args) => { if (args.Data != null) logger.LogInformation($"[Node] {args.Data}"); };
            _nodeProcess.ErrorDataReceived += (sender, args) => { if (args.Data != null) logger.LogError($"[Node] {args.Data}"); };

            _nodeProcess.Start();
            _nodeProcess.BeginOutputReadLine();
            _nodeProcess.BeginErrorReadLine();

            // Wait until Cove signals us to stop
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Cove is shutting down, stopping Node DLNA Bridge...");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to manage Node DLNA Bridge process.");
        }
        finally
        {
            if (_nodeProcess != null && !_nodeProcess.HasExited)
            {
                try { _nodeProcess.Kill(); } catch { /* ignore */ }
                try { _nodeProcess.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private sealed class DlnaExtensionState
    {
        public ExtensionContext Context { get; set; } = null!;
    }
}
