// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.AI.OpenAI;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="IChatClient"/> as a singleton in the services provided by the <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireAzureOpenAIClientBuilderChatClientExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IChatClient"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">An <see cref="AspireAzureOpenAIClientBuilder" />.</param>
    /// <param name="deploymentName">Optionally specifies which model deployment to use. If not specified, a value will be taken from the connection string.</param>
    /// <returns>A <see cref="ChatClientBuilder"/> that can be used to build a pipeline around the inner <see cref="IChatClient"/>.</returns>
    /// <remarks>Reads the configuration from "Aspire.Azure.AI.OpenAI" section.</remarks>
    public static AspireAzureOpenAIClientBuilder AddChatClient(
        this AspireAzureOpenAIClientBuilder builder,
        string? deploymentName = null)
    {
        builder.HostBuilder.Services.AddSingleton(
            services => CreateInnerChatClient(services, builder, deploymentName));

        return builder;
    }

    /// <summary>
    /// Registers a keyed singleton <see cref="IChatClient"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">An <see cref="AspireAzureOpenAIClientBuilder" />.</param>
    /// <param name="serviceKey">The service key with which the <see cref="IChatClient"/> will be registered.</param>
    /// <remarks>Reads the configuration from "Aspire.Azure.AI.OpenAI" section.</remarks>
    public static AspireAzureOpenAIClientBuilder AddKeyedChatClient(
        this AspireAzureOpenAIClientBuilder builder,
        string serviceKey)
    {
        builder.HostBuilder.Services.TryAddKeyedSingleton(
            serviceKey,
            (services, _) => CreateInnerChatClient(services, builder, serviceKey));

        return builder;
    }

    private static IChatClient CreateInnerChatClient(
        IServiceProvider services,
        AspireAzureOpenAIClientBuilder builder,
        string? deploymentName)
    {
        var openAiClient = builder.ServiceKey is null
            ? services.GetRequiredService<AzureOpenAIClient>()
            : services.GetRequiredKeyedService<AzureOpenAIClient>(builder.ServiceKey);

        var settings = builder.GetDeploymentModelSettings();

        deploymentName ??= settings.Models.Keys.FirstOrDefault();

        if (string.IsNullOrEmpty(deploymentName))
        {
            throw new InvalidOperationException($"An {nameof(IChatClient)} could not be configured. Ensure a deployment was defined .");
        }

        if (!settings.Models.TryGetValue(deploymentName, out var _))
        {
            throw new InvalidOperationException($"An {nameof(IChatClient)} could not be configured. Ensure the deployment name '{deploymentName}' was defined .");
        }


        deploymentName ??= builder.GetRequiredDeploymentName();

        var result = openAiClient.AsChatClient(deploymentName);

        return builder.DisableTracing ? result : new OpenTelemetryChatClient(result);
    }
}
