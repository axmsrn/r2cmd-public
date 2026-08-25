using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace R2Cmd;

// DPAPI under the CurrentUser scope: the blob can only be read back by the same
// Windows account on the same machine, which is what a saved SSH password wants.
[SupportedOSPlatform("windows")]
public static class SecurityHelper
{
    // =========================================================================
    // Application entropy.
    //
    // Not a secret — it sits in the binary and anyone who reads it can supply the
    // same bytes. What it does buy is that a blob from this application is not
    // decryptable by simply feeding it to a generic CryptUnprotectData tool, and
    // that another program running as the same user cannot read it by accident.
    //
    // Blobs written before this existed carry no entropy, so decryption falls
    // back to the plain form. Nothing saved earlier is lost; it migrates to the
    // new form the next time the session is saved.
    // =========================================================================
    private static readonly byte[] s_entropy =
        Encoding.UTF8.GetBytes("R2Cmd.SessionCredentials.v1");

    /// <summary>Encrypts a string to Base64. Returns an empty string on failure.</summary>
    public static string EncryptString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";

        byte[]? plainBytes = null;

        try
        {
            plainBytes = Encoding.UTF8.GetBytes(plainText);

            byte[] encryptedBytes = ProtectedData.Protect(
                plainBytes,
                s_entropy,
                DataProtectionScope.CurrentUser);

            return Convert.ToBase64String(encryptedBytes);
        }
        catch
        {
            // Never let a cryptographic failure take the application down
            return "";
        }
        finally
        {
            // The plaintext copy would otherwise sit in the managed heap until
            // some later collection happens to overwrite it
            if (plainBytes != null) CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// Decrypts a Base64 blob. Returns an empty string both when nothing was
    /// stored and when the blob cannot be read — use <see cref="TryDecrypt"/>
    /// when those two need to be told apart.
    /// </summary>
    public static string DecryptString(string cipherText)
    {
        TryDecrypt(cipherText, out string plainText);
        return plainText;
    }

    // =========================================================================
    // Reports whether the value was actually recovered.
    //
    // The distinction matters: a settings file copied to another machine or
    // another Windows account decrypts to nothing, and silently connecting with
    // an empty password produces an authentication error that looks like the
    // server's fault. The caller can instead say the saved password is not
    // available here and ask for it again.
    // =========================================================================
    public static bool TryDecrypt(string cipherText, out string plainText)
    {
        plainText = "";
        if (string.IsNullOrEmpty(cipherText)) return false;

        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromBase64String(cipherText);
        }
        catch (FormatException)
        {
            return false;   // not a blob this class produced
        }

        // Current format first, then the entropy-less blobs written by earlier
        // versions of the application
        return TryUnprotect(cipherBytes, s_entropy, out plainText)
            || TryUnprotect(cipherBytes, null, out plainText);
    }

    private static bool TryUnprotect(byte[] cipherBytes, byte[]? entropy, out string plainText)
    {
        plainText = "";
        byte[]? plainBytes = null;

        try
        {
            plainBytes = ProtectedData.Unprotect(cipherBytes, entropy, DataProtectionScope.CurrentUser);
            plainText = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch (CryptographicException)
        {
            // Wrong entropy, wrong user, or a different machine
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (plainBytes != null) CryptographicOperations.ZeroMemory(plainBytes);
        }
    }
}
