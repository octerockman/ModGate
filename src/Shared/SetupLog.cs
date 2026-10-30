using System;
using System.IO;
using System.Text;

namespace ModGate
{
    internal sealed class SetupLog
    {
        private readonly object gate = new object();
        public readonly string Path;
        public string LastError { get; private set; } = "";
        public SetupLog(string root) { Path = SafePath.Under(root, "ModGate/logs/profile-setup.txt"); }
        public void Start(string operation)
        {
            try
            {
                lock (gate)
                {
                    var folder = System.IO.Path.GetDirectoryName(Path)!; Directory.CreateDirectory(folder);
                    if (File.Exists(Path)) File.Copy(Path, System.IO.Path.Combine(folder, "profile-setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".txt"));
                    File.WriteAllText(Path, "ModGate " + Constants.Version + " - " + operation + Environment.NewLine, new UTF8Encoding(false)); LastError = "";
                }
                Write("Log started. Local time: " + DateTime.Now.ToString("O"));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { LastError = error.Message; }
        }
        public void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    File.AppendAllText(Path, "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { LastError = error.Message; }
        }
        public void Failure(Exception error) => Write("STOPPED: " + error);
    }
}
