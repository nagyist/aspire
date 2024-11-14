// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Codespaces;

internal class CodespacesPortForwarderService(
    ILogger<CodespacesPortForwarderService> logger,
    IOptions<CodespacesOptions> options,
    CodespacesPortForwarder portForwarder,
    DistributedApplicationModel appModel) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsCodespace)
        {
            logger.LogTrace("Not running in Codespaces, skipping port forwarding.");
            return;
        }

        using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while (await periodicTimer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var resource in appModel.Resources.OfType<IResourceWithEndpoints>())
            {
                if (resource.TryGetEndpoints(out var endpoints))
                {
                    var forwardableEndpoints = endpoints.Where(e => e.UriScheme is "http" or "https");
                    foreach (var forwardableEndpoint in forwardableEndpoints)
                    {
                        await portForwarder.ForwardPortsAsync(resource, forwardableEndpoint, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
