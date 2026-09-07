namespace WslcDockerSocket.Tests;

using System.Runtime.InteropServices;
using WslcDockerSocket.Engine;

public sealed class DockerArchitectureTest
{
    [Theory]
    [InlineData(Architecture.X64, "amd64", "x86_64")]
    [InlineData(Architecture.Arm64, "arm64", "aarch64")]
    [InlineData(Architecture.X86, "386", "i386")]
    [InlineData(Architecture.Arm, "arm", "arm")]
    public void DockerApiArchitectureUsesTheActualProcessArchitecture(
        Architecture architecture,
        string expectedVersionArchitecture,
        string expectedInfoArchitecture)
    {
        Assert.Equal(expectedVersionArchitecture, DockerArchitecture.ToVersionArchitecture(architecture));
        Assert.Equal(expectedInfoArchitecture, DockerArchitecture.ToInfoArchitecture(architecture));
    }
}
