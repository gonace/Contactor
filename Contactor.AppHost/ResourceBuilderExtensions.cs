namespace Contactor.AppHost;

public static class ResourceBuilderExtensions
{
    /// <summary>The Docker project the AppHost's containers belong to, so Docker Desktop shows them as one group.</summary>
    public const string DockerProject = "contactor";

    /// <summary>Labels the container the way Docker Compose does, which groups it under <see cref="DockerProject"/>.</summary>
    public static IResourceBuilder<T> InDockerProject<T>(this IResourceBuilder<T> builder) where T : ContainerResource =>
        builder.WithContainerRuntimeArgs(
            "--label", $"com.docker.compose.project={DockerProject}",
            "--label", $"com.docker.compose.service={builder.Resource.Name}");
}
