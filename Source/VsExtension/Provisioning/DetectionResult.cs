using System.Text.Json.Serialization;

namespace PiDbg.Provisioning;

internal sealed class DetectionResult
{
    [JsonPropertyName("schema_version")] public int    SchemaVersion { get; set; }
    [JsonPropertyName("timestamp")]      public string Timestamp     { get; set; } = "";
    [JsonPropertyName("host")]           public DetectionHost       Host       { get; set; } = new();
    [JsonPropertyName("filesystem")]     public DetectionFilesystem Filesystem { get; set; } = new();
    [JsonPropertyName("daemon")]         public DetectionDaemon     Daemon     { get; set; } = new();
    [JsonPropertyName("vsdbg")]          public DetectionVsdbg      Vsdbg      { get; set; } = new();
    [JsonPropertyName("runtime")]        public DetectionRuntime    Runtime    { get; set; } = new();
}

internal sealed class DetectionHost
{
    [JsonPropertyName("arch")]       public string Arch      { get; set; } = "";
    [JsonPropertyName("kernel")]     public string Kernel    { get; set; } = "";
    [JsonPropertyName("os_id")]      public string OsId      { get; set; } = "";
    [JsonPropertyName("os_version")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("os_pretty")]  public string OsPretty  { get; set; } = "";
    [JsonPropertyName("hostname")]   public string Hostname  { get; set; } = "";
    [JsonPropertyName("user")]       public string User      { get; set; } = "";
    [JsonPropertyName("uid")]        public int    Uid       { get; set; }
    [JsonPropertyName("linger")]     public bool   Linger    { get; set; }
}

internal sealed class DetectionFilesystem
{
    [JsonPropertyName("root_exists")]   public bool RootExists   { get; set; }
    [JsonPropertyName("root_writable")] public bool RootWritable { get; set; }
    [JsonPropertyName("free_bytes")]    public long FreeBytes    { get; set; }
}

internal sealed class DetectionDaemon
{
    [JsonPropertyName("binary_exists")]     public bool   BinaryExists     { get; set; }
    [JsonPropertyName("binary_version")]    public string BinaryVersion    { get; set; } = "";
    [JsonPropertyName("binary_sha256")]     public string BinarySha256     { get; set; } = "";
    [JsonPropertyName("service_installed")] public bool   ServiceInstalled { get; set; }
    [JsonPropertyName("service_running")]   public bool   ServiceRunning   { get; set; }
}

internal sealed class DetectionVsdbg
{
    [JsonPropertyName("binary_exists")] public bool   BinaryExists { get; set; }
    [JsonPropertyName("version")]       public string Version      { get; set; } = "";
}

internal sealed class DetectionRuntime
{
    [JsonPropertyName("dotnet_version")]         public string DotnetVersion        { get; set; } = "";
    [JsonPropertyName("systemd_user_available")] public bool   SystemdUserAvailable { get; set; }
    [JsonPropertyName("curl_available")]         public bool   CurlAvailable        { get; set; }
}
