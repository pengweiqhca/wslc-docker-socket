namespace WslcDockerSocket.Engine;

using System.Runtime.InteropServices;

internal static class DockerArchitecture
{
    public static string ToVersionArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "386",
        Architecture.Arm => "arm",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    public static string ToInfoArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "i386",
        Architecture.Arm => "arm",
        _ => architecture.ToString().ToLowerInvariant(),
    };
}
