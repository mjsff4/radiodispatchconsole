using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RadioDispatchConsole
{
    internal sealed class RadioResource
    {
        public string Id, Name;
        public bool XtlTonePreview;
        public RadioResource(string id, string name, bool xtl) { Id = id; Name = name; XtlTonePreview = xtl; }
    }
    // Where the console keeps what it writes. Settings, receive-audio choices, recordings and error logs live in one
    // shared folder (C:\ProgramData\Dispatch Console), so every new version folder finds them. The program folder
    // only holds what ships with a version (exe, cps, branding, maps).
    internal static class AppPaths
    {
        public const string ConfigName = "console.config.xml";
        public static string ProgramDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        private static string _data;
        public static string DataDir { get { if (_data == null) _data = PickDataDir(); return _data; } }
        private static string PickDataDir()
        {
            foreach (Environment.SpecialFolder f in new Environment.SpecialFolder[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                try
                {
                    string d = Path.Combine(Environment.GetFolderPath(f), Brand.DataFolder); Directory.CreateDirectory(d);
                    string probe = Path.Combine(d, ".write-test"); File.WriteAllText(probe, "ok"); File.Delete(probe);
                    return d;
                }
                catch (Exception) { }
            }
            return ProgramDir;   // nowhere shared is writable: keep the old behaviour
        }

        // Folders an older version may have left settings in: this program folder, the version folder above it, and the
        // other version folders beside that one (each checked directly and in its build subfolder). Newest file first.
        public static List<string> PreviousConfigs(string dataDir)
        {
            var found = new List<string>(); var dirs = new List<string>();
            try
            {
                string program = ProgramDir.TrimEnd('\\', '/'); dirs.Add(program);
                string package = Path.GetDirectoryName(program);
                if (!String.IsNullOrEmpty(package))
                {
                    dirs.Add(package);
                    string parent = Path.GetDirectoryName(package);
                    if (!String.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        foreach (string sibling in Directory.GetDirectories(parent)) { dirs.Add(sibling); dirs.Add(Path.Combine(sibling, "build")); }
                }
            }
            catch (Exception) { }
            foreach (string d in dirs)
            {
                try
                {
                    string file = Path.Combine(d, ConfigName);
                    if (File.Exists(file) && !String.Equals(Path.GetFullPath(d).TrimEnd('\\', '/'), Path.GetFullPath(dataDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) && !found.Contains(file)) found.Add(file);
                }
                catch (Exception) { }
            }
            found.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            return found;
        }

        // Copies a settings file and the receive-audio files beside it into dataDir. Returns what was copied.
        public static string CopySettings(string configFile, string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            string from = Path.GetDirectoryName(configFile), target = Path.Combine(dataDir, ConfigName);
            if (File.Exists(target)) File.Copy(target, target + ".before-import", true);
            File.Copy(configFile, target, true);
            int audio = 0;
            foreach (string f in Directory.GetFiles(from, "*.settings"))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("audio.settings", StringComparison.OrdinalIgnoreCase) || (name.StartsWith("audio_", StringComparison.OrdinalIgnoreCase)))
                { File.Copy(f, Path.Combine(dataDir, name), true); audio++; }
            }
            return "settings" + (audio > 0 ? " and " + audio + " receive-audio file(s)" : "") + " from " + from;
        }

        // First start with the shared folder: bring the newest older settings over automatically. "" = nothing imported.
        public static string FirstRunImport(string dataDir)
        {
            try
            {
                if (File.Exists(Path.Combine(dataDir, ConfigName))) return "";
                List<string> previous = PreviousConfigs(dataDir);
                if (previous.Count == 0) return "";
                return CopySettings(previous[0], dataDir);
            }
            catch (Exception ex) { return "could not import older settings: " + ex.Message; }
        }
    }

    internal static class RadioResources
    {
        public static bool ValidId(string id)
        {
            if (String.IsNullOrEmpty(id) || id.Length > 40) return false;
            foreach (char c in id) if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }
        public static List<RadioResource> Load(string path)
        {
            var result = new List<RadioResource>(); var seen = new HashSet<string>();
            if (File.Exists(path))
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length != 3 || !ValidId(parts[0]) || String.IsNullOrWhiteSpace(parts[1]) || !seen.Add(parts[0]))
                        throw new InvalidDataException("Invalid or duplicate radio profile in resources.map.");
                    if (parts[2] != "XTL_PREVIEW" && parts[2] != "RX_ONLY") throw new InvalidDataException("Unknown radio adapter in resources.map.");
                    result.Add(new RadioResource(parts[0], parts[1], parts[2] == "XTL_PREVIEW"));
                }
            if (result.Count == 0) result.Add(new RadioResource("XTL", "Motorola XTL5000", true));
            return result;
        }
        public static void Save(string path, List<RadioResource> resources)
        {
            var lines = new List<string>();
            foreach (RadioResource r in resources) lines.Add(r.Id + "|" + r.Name + "|" + (r.XtlTonePreview ? "XTL_PREVIEW" : "RX_ONLY"));
            // Replace existing profiles atomically. A failed write leaves the old file intact.
            string temp = path + ".tmp"; File.WriteAllLines(temp, lines.ToArray(), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
        public static string AudioFile(string directory, string id)
        {
            if (!ValidId(id)) throw new InvalidDataException("Invalid radio identifier.");
            return Path.Combine(directory, id == "XTL" ? "audio.settings" : "audio_" + id + ".settings");
        }
    }
}
