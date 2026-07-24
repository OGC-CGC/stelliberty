namespace SBproxy.Native.Hub;

public sealed record BootstrapOptions(
    string PipeName,
    string MihomoPath,
    string DataCoreDir,
    string UserDataDir,
    string MihomoPipe,
    string BootstrapYaml);
