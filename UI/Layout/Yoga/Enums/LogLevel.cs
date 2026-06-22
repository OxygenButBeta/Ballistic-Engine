namespace Facebook.Yoga
{
    public enum LogLevel : byte
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3,
        Verbose = 4,
        Fatal = 5,
    }

    public static class LogLevelExtensions
    {
        public const int OrdinalCount = 6;

        public static string ToLogLevelString(this LogLevel level)
        {
            return level switch
            {
                LogLevel.Error => "error",
                LogLevel.Warn => "warn",
                LogLevel.Info => "info",
                LogLevel.Debug => "debug",
                LogLevel.Verbose => "verbose",
                LogLevel.Fatal => "fatal",
                _ => "unknown"
            };
        }
    }
}

