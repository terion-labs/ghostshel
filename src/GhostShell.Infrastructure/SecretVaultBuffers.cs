using System.Security.Cryptography;
using GhostShell.Application;

namespace GhostShell.Infrastructure;

internal static class SecretVaultBuffers
{
    public static byte[] Copy(SecretMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var copy = new byte[material.Length];

        try
        {
            material.CopyTo(copy);
            return copy;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(copy);
            throw;
        }
    }
}
