using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WinDayFlow.Infrastructure.Ai;

internal interface IAiProviderCredentialProtector
{
    ProtectedAiProviderCredential Protect(
        string apiKey,
        Guid profileId,
        long profileRevision,
        string canonicalEndpoint);

    string Unprotect(
        byte[] ciphertext,
        byte[] salt,
        int protectionVersion,
        Guid profileId,
        long profileRevision,
        string canonicalEndpoint);
}

internal readonly record struct ProtectedAiProviderCredential(
    int ProtectionVersion,
    byte[] Ciphertext,
    byte[] Salt);

public sealed class WindowsDpapiCredentialProtector : IAiProviderCredentialProtector
{
    internal const int SaltLength = 32;
    internal const int MaximumCiphertextLength = 65_536;
    internal const int CurrentProtectionVersion = 1;

    private const uint CryptProtectUiForbidden = 0x1;

    private static readonly byte[] EntropyPurpose =
        "WinDayFlow/ai-provider-api-key"u8.ToArray();

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    ProtectedAiProviderCredential IAiProviderCredentialProtector.Protect(
        string apiKey,
        Guid profileId,
        long profileRevision,
        string canonicalEndpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalEndpoint);
        ValidateBinding(profileId, profileRevision);
        EnsureWindows();

        var plaintext = StrictUtf8.GetBytes(apiKey);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var entropy = BuildEntropy(
            profileId,
            profileRevision,
            CurrentProtectionVersion,
            canonicalEndpoint,
            salt);
        try
        {
            return new ProtectedAiProviderCredential(
                CurrentProtectionVersion,
                ProtectCore(plaintext, entropy),
                salt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    string IAiProviderCredentialProtector.Unprotect(
        byte[] ciphertext,
        byte[] salt,
        int protectionVersion,
        Guid profileId,
        long profileRevision,
        string canonicalEndpoint)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalEndpoint);
        ValidateBinding(profileId, profileRevision);
        if (ciphertext.Length is 0 or > MaximumCiphertextLength)
        {
            throw new CryptographicException(
                "The protected AI provider credential has an invalid length.");
        }

        if (salt.Length != SaltLength)
        {
            throw new CryptographicException(
                "The AI provider credential salt has an invalid length.");
        }

        if (protectionVersion != CurrentProtectionVersion)
        {
            throw new CryptographicException(
                "The AI provider credential protection version is not supported.");
        }

        EnsureWindows();
        var entropy = BuildEntropy(
            profileId,
            profileRevision,
            protectionVersion,
            canonicalEndpoint,
            salt);
        byte[] plaintext;
        try
        {
            plaintext = UnprotectCore(ciphertext, entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }

        try
        {
            return StrictUtf8.GetString(plaintext);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CryptographicException(
                "The protected AI provider credential is not valid UTF-8.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] ProtectCore(byte[] plaintext, byte[] entropy)
    {
        var inputBlob = AllocateInput(plaintext);
        var entropyBlob = AllocateInput(entropy);
        DataBlob outputBlob = default;
        try
        {
            if (!CryptProtectData(
                    ref inputBlob,
                    description: null,
                    ref entropyBlob,
                    reserved: IntPtr.Zero,
                    prompt: IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob))
            {
                ThrowDpapiFailure("protect");
            }

            return CopyOutput(outputBlob, MaximumCiphertextLength);
        }
        finally
        {
            FreeInput(ref inputBlob, clear: true);
            FreeInput(ref entropyBlob, clear: true);
            FreeOutput(ref outputBlob, clear: false);
        }
    }

    private static byte[] UnprotectCore(byte[] ciphertext, byte[] entropy)
    {
        var inputBlob = AllocateInput(ciphertext);
        var entropyBlob = AllocateInput(entropy);
        DataBlob outputBlob = default;
        var description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(
                    ref inputBlob,
                    out description,
                    ref entropyBlob,
                    reserved: IntPtr.Zero,
                    prompt: IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob))
            {
                ThrowDpapiFailure("unprotect");
            }

            return CopyOutput(outputBlob, MaximumCiphertextLength);
        }
        finally
        {
            FreeInput(ref inputBlob, clear: false);
            FreeInput(ref entropyBlob, clear: true);
            FreeOutput(ref outputBlob, clear: true);
            if (description != IntPtr.Zero)
            {
                _ = LocalFree(description);
            }
        }
    }

    private static byte[] BuildEntropy(
        Guid profileId,
        long profileRevision,
        int protectionVersion,
        string canonicalEndpoint,
        byte[] salt)
    {
        var profileIdBytes = Encoding.ASCII.GetBytes(profileId.ToString("D"));
        var endpointBytes = StrictUtf8.GetBytes(canonicalEndpoint);
        var entropy = new byte[
            EntropyPurpose.Length
            + profileIdBytes.Length
            + sizeof(int)
            + sizeof(long)
            + sizeof(int)
            + endpointBytes.Length
            + salt.Length];
        var offset = 0;
        EntropyPurpose.CopyTo(entropy, offset);
        offset += EntropyPurpose.Length;
        profileIdBytes.CopyTo(entropy, offset);
        offset += profileIdBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            entropy.AsSpan(offset, sizeof(int)),
            protectionVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt64BigEndian(
            entropy.AsSpan(offset, sizeof(long)),
            profileRevision);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32BigEndian(
            entropy.AsSpan(offset, sizeof(int)),
            endpointBytes.Length);
        offset += sizeof(int);
        endpointBytes.CopyTo(entropy, offset);
        offset += endpointBytes.Length;
        salt.CopyTo(entropy, offset);
        return entropy;
    }

    private static DataBlob AllocateInput(byte[] bytes)
    {
        var blob = new DataBlob
        {
            Length = bytes.Length,
            Data = Marshal.AllocHGlobal(bytes.Length),
        };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static byte[] CopyOutput(DataBlob blob, int maximumLength)
    {
        if (blob.Data == IntPtr.Zero || blob.Length is <= 0 || blob.Length > maximumLength)
        {
            throw new CryptographicException(
                "Windows DPAPI returned an invalid credential payload.");
        }

        var bytes = new byte[blob.Length];
        Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void FreeInput(ref DataBlob blob, bool clear)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (clear)
        {
            ClearUnmanaged(blob.Data, blob.Length);
        }

        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    private static void FreeOutput(ref DataBlob blob, bool clear)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (clear)
        {
            ClearUnmanaged(blob.Data, blob.Length);
        }

        _ = LocalFree(blob.Data);
        blob = default;
    }

    private static void ClearUnmanaged(IntPtr data, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(data, index, 0);
        }
    }

    private static void ValidateBinding(Guid profileId, long profileRevision)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A protected AI provider credential requires a profile identifier.",
                nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(profileRevision);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "AI provider credentials require Windows DPAPI.");
        }
    }

    private static void ThrowDpapiFailure(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        throw new CryptographicException(
            $"Windows DPAPI could not {operation} the AI provider credential.",
            new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

#pragma warning disable SYSLIB1054
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        out IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
}
