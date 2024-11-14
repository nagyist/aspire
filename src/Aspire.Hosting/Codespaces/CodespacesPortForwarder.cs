// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Codespaces;

internal class CodespacesPortForwarder(IOptions<CodespacesOptions> options)
{
    public async Task ForwardPortsAsync(IResource resource, EndpointAnnotation endpointAnnotation, CancellationToken cancellationToken)
    {
        _ = resource;
        _ = endpointAnnotation;

        if (!options.Value.IsCodespace)
        {
            return;
        }

        var requestUri = $"{options.Value.ApiUrl}/user/codespaces/{options.Value.CodespaceName}?internal=true"; // Adjust the URI as needed
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("User-Agent", "Aspire.Hosting.Codespaces");
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Add("Authorization", $"Bearer {options.Value.CodespaceToken}");

        using var httpClient = new HttpClient();
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var jsonResponse = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
    }
}
