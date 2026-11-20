using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Sfera {
    internal static class ProAccess {
        const long Week = 7 * 24 * 60 * 60;
        static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly object gate = new object();
        static readonly string path = Path.Combine(Store.Home, "pro", "subscription.dat");
        static readonly Regex devicePattern = new Regex(@"\A[A-Za-z0-9_-]{24}\z");
        static readonly Regex number = new Regex(@"\A[0-9]{10}\z");
        static readonly Stopwatch elapsed = Stopwatch.StartNew();
        static readonly long started = UtcSeconds();
        static State state;
        static string device;
        static long validUntil;
        sealed class State {
            public string Seed { get; set; }
            public string Receipt { get; set; }
            public long LastSeen { get; set; }
        }
        static long UtcSeconds() { return (long)(DateTime.UtcNow - epoch).TotalSeconds; }
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
            var fresh = new State { Seed = Base64Url(seed), LastSeen = UtcSeconds() };
            Write(fresh); return fresh;
        }
        static void Load() {
            if (state != null) return;
            var value = Read();
            using (var id = WindowsIdentity.GetCurrent()) using (var hash = SHA256.Create()) {
                string machine = (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "");
                device = Base64Url(hash.ComputeHash(Encoding.UTF8.GetBytes(value.Seed + "|" + machine + "|" + id.User.Value)).Take(18).ToArray());
            }
            long expiry;
            if (Verify(value.Receipt, device, UtcSeconds(), out expiry)) validUntil = expiry;
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
        internal static bool Verify(string token, string expected, long now, out long expiry) {
            expiry = 0;
            if (string.IsNullOrEmpty(token) || token.Length > 1024) return false;
            var parts = token.Trim().Split('.');
            if (parts.Length != 5 || parts[0] != "OP1" || !devicePattern.IsMatch(parts[1]) || parts[1] != expected || !number.IsMatch(parts[2]) || !number.IsMatch(parts[3])) return false;
            long issued = long.Parse(parts[2], CultureInfo.InvariantCulture), end = long.Parse(parts[3], CultureInfo.InvariantCulture);
            if (issued > now + 300 || end <= now || end <= issued || end - issued > Week) return false;
            if (!Signed(string.Join(".", parts, 0, 4), parts[4])) return false;
            expiry = end; return true;
        }
        internal static string Device { get { lock (gate) { Load(); return device; } } }
        static long Now() {
            long clock = UtcSeconds();
            if (clock + 300 < state.LastSeen) throw new InvalidOperationException("Проверьте дату и время Windows и снова откройте Pro.");
            return Math.Max(state.LastSeen, Math.Max(clock, started + (long)elapsed.Elapsed.TotalSeconds));
        }
        internal static bool Active { get { lock (gate) {
            Load(); long now = Now();
            if (validUntil <= now) return false;
            if (now - state.LastSeen >= 900) { state.LastSeen = now; Write(state); }
            return true;
        } } }
        internal static DateTime Expires { get { lock (gate) { Load(); return epoch.AddSeconds(validUntil).ToLocalTime(); } } }
        internal static void Require() { if (!Active) throw new UnauthorizedAccessException("Для рабочего пространства подтвердите подписку на канал в разделе OSTUM Pro."); }
        internal static void Import(string token) {
            lock (gate) {
                Load(); long now = Now(), end;
                if (!Verify(token, device, now, out end)) throw new InvalidDataException("Этот ответ не подходит. Запросите у бота новый файл для кода, показанного на этом компьютере.");
                if (validUntil >= end) return;
                var update = new State { Seed = state.Seed, Receipt = token.Trim(), LastSeen = now };
                Write(update); state = update; validUntil = end;
            }
        }
    }
}
