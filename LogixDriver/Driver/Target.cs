using libplctag;

namespace Logix.Driver
{
    public class Target
    {
        public string Name { get; }
        public string Gateway { get; }
        public string Path { get; }
        public PlcType PlcType { get; }
        public int TimeoutMs { get; set; } = 5000;

        /// <summary>
        /// Interval between background heartbeat probes used to detect connection loss while idle.
        /// <see cref="TimeSpan.Zero"/> disables the heartbeat; loss is then only detected during reads/writes.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.Zero;

        public Target(
            string name,
            string gateway,
            string path = "1,0",
            PlcType plcType = PlcType.ControlLogix,
            int timeoutMs = 5000,
            TimeSpan heartbeatInterval = default)
        {
            Name = name;
            Gateway = gateway;
            Path = path;
            PlcType = plcType;
            TimeoutMs = timeoutMs;
            HeartbeatInterval = heartbeatInterval;
        }
    }
}
