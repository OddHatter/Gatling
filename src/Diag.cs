namespace Oddhatter.MachineArbalest
{
    /// <summary>
    /// Opt-in diagnostics (Diagnostics.DebugLogging). Each channel logs only when its message changes, so
    /// per-step calls don't flood the log.
    /// </summary>
    internal static class Diag
    {
        private static readonly System.Collections.Generic.Dictionary<string, string> s_last =
            new System.Collections.Generic.Dictionary<string, string>();

        public static void Log(string channel, string message)
        {
            if (!MachineArbalestPlugin.DebugLogging.Value)
            {
                return;
            }
            if (s_last.TryGetValue(channel, out string last) && last == message)
            {
                return;
            }
            s_last[channel] = message;
            MachineArbalestPlugin.Log.LogInfo(message);
        }

        /// <summary>Let the next message on this channel log even if it repeats the previous one.</summary>
        public static void Forget(string channel)
        {
            s_last.Remove(channel);
        }
    }
}
