using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cronator
{
    public enum LogLevel { Trace=0, Debug=1, Info=2, Warn=3, Error=4, None=5 }

    /// <summary>
    /// Minimal, loop-safe logger:
    /// - Console + daily rolling file (logs/cronator-YYYYMMDD.log)
    /// - Category + level filtering (env, runtime)
    /// - Optional Console/Trace capture WITHOUT recursion
    /// - Thread-safe; timing scopes
    /// </summary>
    public static class Log
    {
        private static readonly object _fileLock = new();
        private static volatile LogLevel _level = LogLevel.Info;
        private static volatile bool _toFile = true;
        private static volatile bool _captureConsole = false;

        private static string _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static string _currentFile = "";

        private static readonly ConcurrentDictionary<string, bool> _enabledCats  = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> _disabledCats = new(StringComparer.OrdinalIgnoreCase);

        // Reentrancy guard (per thread)
        [ThreadStatic] private static bool _inWrite;

        // Keep originals so we never write to intercepted streams
        private static TextWriter? _origOut;
        private static TextWriter? _origErr;

        public static LogLevel Level
        {
            get => _level;
            set => _level = value;
        }

        /// <summary>Initialize from environment vars (optional).</summary>
        public static void Init(
            string? logDir = null,
            LogLevel? level = null,
            bool? writeToFile = null,
            bool captureConsole = false)
        {
            if (!string.IsNullOrWhiteSpace(logDir)) _logDir = logDir;
            if (level is { } lv) _level = lv;
            if (writeToFile is { } wf) _toFile = wf;

            // env overrides
            var envLevel = Environment.GetEnvironmentVariable("CRONATOR_LOG_LEVEL");
            if (Enum.TryParse<LogLevel>(envLevel, true, out var envLv)) _level = envLv;

            var envCats = Environment.GetEnvironmentVariable("CRONATOR_LOG_CATS"); // "Monitors,Layout,Clock"
            if (!string.IsNullOrWhiteSpace(envCats))
                foreach (var c in envCats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    EnableCategory(c);

            var envNoCats = Environment.GetEnvironmentVariable("CRONATOR_LOG_DISABLE_CATS"); // "Noise"
            if (!string.IsNullOrWhiteSpace(envNoCats))
                foreach (var c in envNoCats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    DisableCategory(c);

            Directory.CreateDirectory(_logDir);
            _currentFile = BuildFilePath(DateTime.Now);

            // Save originals once
            _origOut ??= Console.Out;
            _origErr ??= Console.Error;

            _captureConsole = captureConsole;
            if (_captureConsole)
            {
                Console.SetOut(new ConsoleInterceptWriter(_origOut!, s => RawWrite(LogLevel.Info, "CLI", "WriteLine", s)));
                Console.SetError(new ConsoleInterceptWriter(_origErr!, s => RawWrite(LogLevel.Error, "CLI", "WriteLine", s)));
            }
            else
            {
                // restore in case it was previously captured
                if (!ReferenceEquals(Console.Out, _origOut) && _origOut != null) Console.SetOut(_origOut);
                if (!ReferenceEquals(Console.Error, _origErr) && _origErr != null) Console.SetError(_origErr);
            }

            // Route System.Diagnostics.Trace WITHOUT calling Log.* (raw path, no recursion)
            Trace.Listeners.Clear();
            Trace.Listeners.Add(new TextWriterTraceListener(new TraceInterceptWriter(s => RawWrite(LogLevel.Debug, "Trace", null, s))));
            Trace.AutoFlush = true;
        }

        public static void EnableCategory(string cat)  => _enabledCats[cat] = true;
        public static void DisableCategory(string cat) => _disabledCats[cat] = true;

        public static bool IsEnabled(string? cat, LogLevel level)
        {
            if (level < _level) return false;
            if (string.IsNullOrEmpty(cat)) return true;
            if (_disabledCats.ContainsKey(cat)) return false;
            if (_enabledCats.IsEmpty) return true; // no allowlist => all cats on
            return _enabledCats.ContainsKey(cat);
        }

        // ---- Core API ----
        public static void T(string cat, string msg, Exception? ex=null, [CallerMemberName] string? m=null) => Write(LogLevel.Trace, cat, msg, ex, m);
        public static void D(string cat, string msg, Exception? ex=null, [CallerMemberName] string? m=null) => Write(LogLevel.Debug, cat, msg, ex, m);
        public static void I(string cat, string msg, Exception? ex=null, [CallerMemberName] string? m=null) => Write(LogLevel.Info,  cat, msg, ex, m);
        public static void W(string cat, string msg, Exception? ex=null, [CallerMemberName] string? m=null) => Write(LogLevel.Warn,  cat, msg, ex, m);
        public static void E(string cat, string msg, Exception? ex=null, [CallerMemberName] string? m=null) => Write(LogLevel.Error, cat, msg, ex, m);

        public static IDisposable Timed(string cat, string scope, LogLevel level = LogLevel.Debug)
            => new LogScope(cat, scope, level);

        private static void Write(LogLevel level, string? cat, string msg, Exception? ex, string? member)
        {
            if (!IsEnabled(cat, level)) return;
            if (_inWrite) return; // hard stop on any recursion
            _inWrite = true;
            try
            {
                var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                var sb = new StringBuilder()
                    .Append('[').Append(ts).Append("] ")
                    .Append('[').Append(level.ToString().ToUpperInvariant()).Append("] ");
                if (!string.IsNullOrEmpty(cat)) sb.Append('[').Append(cat).Append("] ");
                if (!string.IsNullOrEmpty(member)) sb.Append(member).Append(": ");
                sb.Append(msg);

                if (ex != null)
                {
                    sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                    if (level >= LogLevel.Debug && !string.IsNullOrEmpty(ex.StackTrace))
                        sb.AppendLine().Append(ex.StackTrace);
                }

                var text = sb.ToString();

                // Console sink
                WriteToConsole(text, isError: level >= LogLevel.Error, level);

                // File sink
                if (_toFile) WriteToFile(text);
            }
            finally
            {
                _inWrite = false;
            }
        }

        // Raw write that BYPASSES Log.* — used by console/trace capture
        private static void RawWrite(LogLevel level, string category, string? member, string message)
        {
            if (_inWrite) return;
            _inWrite = true;
            try
            {
                // Respect level/category filters
                if (!IsEnabled(category, level)) return;

                var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                var line = new StringBuilder()
                    .Append('[').Append(ts).Append("] ")
                    .Append('[').Append(level.ToString().ToUpperInvariant()).Append("] ")
                    .Append('[').Append(category).Append("] ");
                if (!string.IsNullOrEmpty(member)) line.Append(member).Append(": ");
                line.Append(message)
                    .ToString();

                var text = line.ToString();

                // When capturing, NEVER write via Console.* (that would re-enter the intercept).
                // Use the original writers directly.
                var tw = level >= LogLevel.Error ? _origErr : _origOut;
                tw?.WriteLine(text);

                if (_toFile) WriteToFile(text);
            }
            finally
            {
                _inWrite = false;
            }
        }

        private static void WriteToFile(string text)
        {
            try
            {
                var todayPath = BuildFilePath(DateTime.Now);
                if (!string.Equals(todayPath, _currentFile, StringComparison.OrdinalIgnoreCase))
                    _currentFile = todayPath; // daily rollover

                lock (_fileLock)
                {
                    File.AppendAllText(_currentFile, text + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* never throw from logging */ }
        }

        private static void WriteToConsole(string line, bool isError, LogLevel level)
        {
            try
            {
                if (_captureConsole)
                {
                    // Avoid Console.WriteLine while captured (it would loop)
                    var tw = isError ? _origErr : _origOut;
                    tw?.WriteLine(line);
                    return;
                }

                // Not capturing -> safe to use Console and colors.
                var save = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Warn  => ConsoleColor.Yellow,
                    LogLevel.Debug => ConsoleColor.Gray,
                    LogLevel.Trace => ConsoleColor.DarkGray,
                    _ => Console.ForegroundColor
                };
                Console.WriteLine(line);
                Console.ForegroundColor = save;
            }
            catch { /* headless / redirected */ }
        }

        private static string BuildFilePath(DateTime dt) => Path.Combine(_logDir, $"cronator-{dt:yyyyMMdd}.log");

        // ---- helpers ----
        private sealed class LogScope : IDisposable
        {
            private readonly string _cat, _scope;
            private readonly LogLevel _level;
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            public LogScope(string cat, string scope, LogLevel level)
            {
                _cat = cat; _scope = scope; _level = level;
                I(_cat,$"→ {_scope}");
            }
            public void Dispose()
            {
                _sw.Stop();
                Write(_level,_cat,$"← {_scope} ({_sw.ElapsedMilliseconds} ms)", null, null);
            }
        }

        // Intercepts Console.* and mirrors to original stream + file via RawWrite (no Log.* calls)
        private sealed class ConsoleInterceptWriter : TextWriter
        {
            private readonly TextWriter _passthrough;
            private readonly Action<string> _onLine;
            public ConsoleInterceptWriter(TextWriter passthrough, Action<string> onLine)
            {
                _passthrough = passthrough; _onLine = onLine;
            }
            public override Encoding Encoding => _passthrough.Encoding;

            public override void WriteLine(string? value)
            {
                // First keep app behavior identical
                _passthrough.WriteLine(value);
                // Then mirror to logs (raw path)
                _onLine(value ?? string.Empty);
            }
            public override void Write(char value) => _passthrough.Write(value);
            public override void Write(string? value) => _passthrough.Write(value);
        }

        // Intercepts Trace.* and mirrors via RawWrite (no Log.* calls)
        private sealed class TraceInterceptWriter : TextWriter
        {
            private readonly Action<string> _onLine;
            public TraceInterceptWriter(Action<string> onLine) { _onLine = onLine; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void WriteLine(string? value) => _onLine(value ?? string.Empty);
            public override void Write(string? value)    => _onLine(value ?? string.Empty);
        }
    }
}
