// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Codespaces;

internal class CodespacesAutoUpdatePortForwardingHook(DistributedApplicationExecutionContext executionContext, IOptions<CodespacesOptions> options, ILogger<CodespacesAutoUpdatePortForwardingHook> logger) : IDistributedApplicationLifecycleHook
{
    public async Task AfterEndpointsAllocatedAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        if (executionContext.IsPublishMode || !options.Value.IsCodespace)
        {
            return;
        }

        var endpointResources = appModel.Resources.OfType<IResourceWithEndpoints>();

        var endpointsThatNeedForwarding = new List<EndpointAnnotation>();
        foreach (var endpointResource in endpointResources)
        {
            if (endpointResource.TryGetEndpoints(out var resourceEndpoints))
            {
                var httpAndHttpsEndpointsWithExplicitPorts = resourceEndpoints.Where(
                    e => e.UriScheme == "http" || e.UriScheme == "https" && e.Port != null
                );

                endpointsThatNeedForwarding.AddRange(httpAndHttpsEndpointsWithExplicitPorts);
            }
        }

        var gitRootPath = FindGitRoot(Environment.CurrentDirectory);

        if (gitRootPath == null)
        {
            return;
        }

        var devContainerConfigurationPath = Path.Combine(gitRootPath, ".devcontainer", "devcontainer.json");

        if (File.Exists(devContainerConfigurationPath))
        {
            var devContainerConfigurationContent = await File.ReadAllTextAsync(devContainerConfigurationPath, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(devContainerConfigurationContent) is { } rootNode)
            {
                if (rootNode["forwardPorts"] is not JsonArray forwardPortsArray)
                {
                    forwardPortsArray = new JsonArray();
                    rootNode["forwardPorts"] = forwardPortsArray;
                }

                if (rootNode["portsAttributes"] is not JsonObject portsAttributesObject)
                {
                    portsAttributesObject = new JsonObject();
                    rootNode["portsAttributes"] = portsAttributesObject;
                }

                foreach (var endpoint in endpointsThatNeedForwarding)
                {
                    if (endpoint.Port is not {} port)
                    {
                        continue;
                    }

                    forwardPortsArray.Add(endpoint.Port);

                    var portPropertyName = port.ToString(CultureInfo.InvariantCulture);
                    if (!portsAttributesObject.TryGetPropertyValue(portPropertyName, out var portAttributes))
                    {
                        portAttributes = new JsonObject();
                        portsAttributesObject[portPropertyName] = portAttributes;
                    }

                    portAttributes!["protocol"] = endpoint.UriScheme;
                }

                await File.WriteAllTextAsync(devContainerConfigurationPath, rootNode.ToString(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    static string? FindGitRoot(string? directory)
    {
        if (directory == null)
        {
            return null;
        }

        if (Directory.Exists(Path.Combine(directory, ".git")))
        {
            return directory;
        }

        string? parentDirectory = Directory.GetParent(directory)?.FullName;
        return FindGitRoot(parentDirectory);
    }
}
