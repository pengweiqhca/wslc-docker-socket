namespace WslcDockerSocket.Engine;

using Api;

internal readonly struct DockerImageReference : IEquatable<DockerImageReference>
{
    private DockerImageReference(string canonicalName)
    {
        CanonicalName = canonicalName;
    }

    public string CanonicalName { get; }

    public static DockerImageReference FromPullQuery(IQueryCollection query)
    {
        var image = query["fromImage"].ToString();
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest, "fromImage is required");
        }

        var tag = query["tag"].ToString();
        if (!string.IsNullOrWhiteSpace(tag) && !image.Contains('@') && image.LastIndexOf(':') <= image.LastIndexOf('/'))
        {
            image += ":" + tag;
        }

        return Parse(image);
    }

    public static DockerImageReference Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest, "Image is required");
        }

        var reference = value.Trim();
        var digestIndex = reference.LastIndexOf('@');
        var digest = digestIndex < 0 ? string.Empty : reference[digestIndex..].ToLowerInvariant();
        var nameAndTag = digestIndex < 0 ? reference : reference[..digestIndex];
        var components = nameAndTag.Split('/');
        var hasRegistry = components.Length > 1 && IsRegistry(components[0]);
        var registry = hasRegistry ? NormalizeRegistry(components[0]) : "docker.io";
        var repository = hasRegistry ? string.Join("/", components.Skip(1)) : nameAndTag;
        if (registry == "docker.io" && !repository.Contains('/'))
        {
            repository = "library/" + repository;
        }

        var lastColon = repository.LastIndexOf(':');
        var lastSlash = repository.LastIndexOf('/');
        if (digest.Length == 0 && lastColon <= lastSlash)
        {
            repository += ":latest";
        }

        return new DockerImageReference(registry + "/" + repository.ToLowerInvariant() + digest);
    }

    public bool Matches(string image) => Equals(Parse(image));

    public bool Equals(DockerImageReference other) => StringComparer.Ordinal.Equals(CanonicalName, other.CanonicalName);

    public override bool Equals(object? obj) => obj is DockerImageReference other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalName);

    private static bool IsRegistry(string component) => component.Contains('.') || component.Contains(':')
                                                        || component.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRegistry(string registry) => registry.Equals("index.docker.io", StringComparison.OrdinalIgnoreCase)
        || registry.Equals("registry-1.docker.io", StringComparison.OrdinalIgnoreCase)
        ? "docker.io"
        : registry.ToLowerInvariant();
}
