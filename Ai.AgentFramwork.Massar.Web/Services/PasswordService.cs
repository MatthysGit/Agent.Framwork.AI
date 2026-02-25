using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;

namespace Ai.AgentFramwork.Massar.Web.Services.Security
{
    public interface IPasswordService
    {
        string Hash(string password);
        bool Verify(string password, string stored);
    }

    public sealed class PasswordService : IPasswordService
    {
        public string Hash(string password)
        {
            // Example format: v1.<saltB64>.<hashB64>
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations: 100_000,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: 32);

            return $"v1.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        public bool Verify(string password, string stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return false;

            // ✅ Legacy support: if it isn't in our "v1.*.*" format, treat as plain text
            if (!stored.StartsWith("v1.", StringComparison.Ordinal))
                return stored == password;

            try
            {
                var parts = stored.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) return false;

                var salt = Convert.FromBase64String(parts[1]);
                var expected = Convert.FromBase64String(parts[2]);

                var actual = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    iterations: 100_000,
                    hashAlgorithm: HashAlgorithmName.SHA256,
                    outputLength: expected.Length);

                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                // ✅ Never crash login
                return false;
            }
        }
    }
}
