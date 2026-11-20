using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Threading;

namespace Sfera {
    internal static class ProAccess {
        const long Week = 7 * 24 * 60 * 60;
        static readonly object gate = new object();
        static readonly string path = Path.Combine(Store.Home, "pro", "subscription.dat");
        static readonly Regex devicePattern = new Regex(@"\A[A-Za-z0-9_-]{24}\z");
        static readonly Regex number = new Regex(@"\A[0-9]{10}\z");
        static State state;
        static string device;
        static bool active;
        sealed class State {
            public string Seed { get; set; }
            public string Receipt { get; set; }
        }
        static string Base64Url(byte[] data) { return Convert.ToBase64String(data).TrimEnd('=').Replace('+','-').Replace('/','_'); }
        static byte[] Decode(string text) {
            string data = text.Replace('-','+').Replace('_','/');
            return Convert.FromBase64String(data.PadRight((data.Length + 3) / 4 * 4, '='));
        }
        static void Write(State value) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] bytes = Encoding.UTF8.GetBytes(Store.Json().Serialize(value));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllBytes(temp, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        static State Read() {
            try {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                var saved = Store.Json().Deserialize<State>(Encoding.UTF8.GetString(bytes));
                if (saved != null && saved.Seed != null && Decode(saved.Seed).Length == 32) return saved;
            } catch (FileNotFoundException) {} catch (DirectoryNotFoundException) {} catch (CryptographicException) {} catch (FormatException) {} catch (ArgumentException) {}
            var seed = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(seed);
            var fresh = new State { Seed = Base64Url(seed) };
            Write(fresh); return fresh;
        }
        static void Load() {
            if (state != null) return;
            State value;
            using (var creation = new Mutex(false, @"Local\OSTUM.ProState." + WindowsIdentity.GetCurrent().User.Value)) {
                try { creation.WaitOne(); } catch (AbandonedMutexException) {}
                try { value = Read(); } finally { creation.ReleaseMutex(); }
            }
            using (var id = WindowsIdentity.GetCurrent()) using (var hash = SHA256.Create()) {
                string machine = (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "");
                device = Base64Url(hash.ComputeHash(Encoding.UTF8.GetBytes(value.Seed + "|" + machine + "|" + id.User.Value)).Take(18).ToArray());
            }
            active = Verify(value.Receipt, device);
            state = value;
        }
        static bool Signed(string body, string signature) {
            if (string.IsNullOrEmpty(signature) || signature.Length > 600 || !Regex.IsMatch(signature,@"\A[A-Za-z0-9_-]+\z")) return false;
            try {
                using (var rsa = new RSACryptoServiceProvider(new CspParameters(24) { Flags = CspProviderFlags.CreateEphemeralKey })) {
                    rsa.FromXmlString(Store.Resource("issuer-public.xml"));
                    return rsa.VerifyData(Encoding.ASCII.GetBytes(body), "SHA256", Decode(signature));
                }
            } catch (CryptographicException) { return false; } catch (FormatException) { return false; }
        }
        internal static bool Verify(string token, string expected) {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return false;
            var parts = token.Trim().Split('.');
            if (parts.Length != 5 || !devicePattern.IsMatch(parts[1]) || parts[1] != expected || !number.IsMatch(parts[2])) return false;
            if (parts[0] == "OP1") {
                // A signed 5.1 receipt proves that this installation passed its first check.
                if (!number.IsMatch(parts[3])) return false;
                long span = long.Parse(parts[3], CultureInfo.InvariantCulture) - long.Parse(parts[2], CultureInfo.InvariantCulture);
                if (span <= 0 || span > Week) return false;
            } else if (parts[0] != "OP2" || parts[3] != "permanent") return false;
            return Signed(string.Join(".", parts, 0, 4), parts[4]);
        }
        internal static string Device { get { lock (gate) { Load(); return device; } } }
        internal static bool Active { get { lock (gate) { Load(); return active; } } }
        internal static void Require() {
            if (!Active) throw new UnauthorizedAccessException("Для первого входа в рабочее пространство активируйте OSTUM Pro.");
        }
        internal static void Import(string token) {
            lock (gate) {
                Load();
                if (!Verify(token, device)) throw new InvalidDataException("Файл не подходит к этой установке. Откройте QR на этом ПК и перенесите файл из ответа бота.");
                if (active) return;
                var update = new State { Seed = state.Seed, Receipt = token.Trim() };
                Write(update); state = update; active = true;
            }
        }
    }
}
