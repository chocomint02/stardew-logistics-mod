using System;
using StardewModdingAPI;

namespace StardewLogistics.Framework
{
    /// <summary>The mod's logging entry point.</summary>
    /// <remarks>
    /// The interesting things to log happen inside menus and the network scanner, neither of which has a natural
    /// route back to <see cref="Mod.Monitor"/> without threading a reference through several constructors that
    /// otherwise have no use for it. This mirrors <see cref="ItemSource"/>: one explicit init call at startup, and
    /// a no-op before that rather than a null reference.
    /// </remarks>
    internal static class Log
    {
        /*********
        ** Fields
        *********/
        private static IMonitor Monitor;


        /*********
        ** Public methods
        *********/
        /// <summary>Wires up the logger. Call once from the mod entry point.</summary>
        public static void Initialise(IMonitor monitor) => Monitor = monitor;

        /// <summary>Logs diagnostic detail, which SMAPI writes to the log file but not the console.</summary>
        public static void Trace(string message) => Monitor?.Log(message, LogLevel.Trace);

        /// <summary>Logs something the player might want to see in the console.</summary>
        public static void Debug(string message) => Monitor?.Log(message, LogLevel.Debug);

        /// <summary>Logs a problem that didn't stop the mod working.</summary>
        public static void Warn(string message) => Monitor?.Log(message, LogLevel.Warn);

        /// <summary>Logs a failure, including the exception detail at trace level.</summary>
        public static void Error(string message, Exception ex = null)
        {
            Monitor?.Log(message, LogLevel.Error);
            if (ex != null)
                Monitor?.Log(ex.ToString(), LogLevel.Error);
        }
    }
}
