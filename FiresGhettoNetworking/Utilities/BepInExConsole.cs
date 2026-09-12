using System;
using System.IO;
using System.Reflection;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Direct writer for the BepInEx console. ConsoleManager is internal, so the stream and the colour
    /// setter are resolved once by reflection; if that fails both stay null and callers fall back to
    /// plain logging.
    /// </summary>
    internal static class BepInExConsole
    {
        private static bool _resolved;
        private static Func<object> _streamGetter;
        private static Action<ConsoleColor> _setColor;

        /// <summary>The console stream, or null when colour output is unavailable.</summary>
        public static TextWriter Stream
        {
            get
            {
                Resolve();
                if (_streamGetter == null || _setColor == null) return null;
                return _streamGetter() as TextWriter;
            }
        }

        public static void SetColor(ConsoleColor color)
        {
            Resolve();
            if (_setColor != null) _setColor(color);
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                var assembly = typeof(BepInEx.Logging.ConsoleLogListener).Assembly;
                var consoleManager = assembly.GetType("BepInEx.ConsoleManager", throwOnError: false);
                if (consoleManager == null) return;

                var streamProperty = consoleManager.GetProperty("ConsoleStream",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var streamGetter = streamProperty?.GetGetMethod(nonPublic: true);
                if (streamGetter != null)
                    _streamGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), streamGetter);

                var setColorMethod = consoleManager.GetMethod("SetConsoleColor",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(ConsoleColor) },
                    modifiers: null);
                if (setColorMethod != null)
                    _setColor = (Action<ConsoleColor>)Delegate.CreateDelegate(typeof(Action<ConsoleColor>), setColorMethod);
            }
            catch
            {
                // Delegates stay null; every caller already has a plain-text fallback.
            }
        }
    }
}
