namespace PKS.Infrastructure.Services.Models;

public class FirecrackerRunnerRegistration
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Project { get; set; } = "";
    public string Server { get; set; } = "";
    public DateTime RegisteredAt { get; set; }
}

public class FirecrackerRunnerConfiguration
{
    public List<FirecrackerRunnerRegistration> Registrations { get; set; } = new();
    public FirecrackerDefaults Defaults { get; set; } = new();
    public DateTime? LastModified { get; set; }
}

public class FirecrackerDefaults
{
    public int DefaultVcpus { get; set; } = 2;
    public int DefaultMemMib { get; set; } = 2048;
    public string KernelPath { get; set; } = "";
    public string BaseRootfsPath { get; set; } = "";
    public string NetworkSubnet { get; set; } = "172.16.0.0/16";
    public string WorkDir { get; set; } = "";
}

public class FirecrackerVmConfig
{
    public int VcpuCount { get; set; }
    public int MemSizeMib { get; set; }
    public string KernelPath { get; set; } = "";
    public string RootfsPath { get; set; } = "";
    public string TapDevice { get; set; } = "";
    public string VmIpAddress { get; set; } = "";
    public string GatewayIp { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string SocketPath { get; set; } = "";
}

public class FirecrackerVmState
{
    public string VmId { get; set; } = "";
    public string JobId { get; set; } = "";
    public int ProcessId { get; set; }
    public string TapDevice { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public FirecrackerVmStatus Status { get; set; }
}

public enum FirecrackerVmStatus
{
    Booting,
    Running,
    Completed,
    Failed,
    Cleaning
}

public class FirecrackerSpawnJobPayload
{
    public int MemMib { get; set; }
    public int VcpuCount { get; set; }
    public string Command { get; set; } = "";
    public string JobType { get; set; } = "";
}
