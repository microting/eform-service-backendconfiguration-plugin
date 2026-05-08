/*
The MIT License (MIT)

Copyright (c) 2007 - 2026 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Security.Cryptography;
using System.Text;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Mirrors <c>BackendConfiguration.Pn.Services.GoogleDrive.GoogleDriveAuthService.Decrypt</c>.
/// AES-GCM at-rest format on <c>GoogleOAuthToken.EncryptedRefreshToken</c>:
/// <c>base64(nonce(12) || ciphertext || tag(16))</c>. Key supplied as a
/// base64-encoded 32-byte value via <c>GoogleDrive:RefreshTokenEncryptionKey</c>.
///
/// Code is duplicated from the plugin (rather than moved into the base
/// package) for PR-6 scope only — keeping the base package surface stable.
/// </summary>
internal static class RefreshTokenCrypto
{
    private const int AesGcmNonceSize = 12;
    private const int AesGcmTagSize = 16;

    public static string Decrypt(string ciphertextB64, string encryptionKeyBase64)
    {
        if (string.IsNullOrEmpty(encryptionKeyBase64))
        {
            throw new InvalidOperationException(
                "GoogleDrive:RefreshTokenEncryptionKey is not configured.");
        }

        var key = Convert.FromBase64String(encryptionKeyBase64);
        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"GoogleDrive:RefreshTokenEncryptionKey must decode to 32 bytes (got {key.Length}).");
        }

        var plaintext = Array.Empty<byte>();
        try
        {
            var combined = Convert.FromBase64String(ciphertextB64);
            if (combined.Length < AesGcmNonceSize + AesGcmTagSize)
            {
                throw new InvalidOperationException("Encrypted blob is malformed.");
            }

            var ctLen = combined.Length - AesGcmNonceSize - AesGcmTagSize;
            var nonce = new byte[AesGcmNonceSize];
            var ciphertext = new byte[ctLen];
            var tag = new byte[AesGcmTagSize];
            Buffer.BlockCopy(combined, 0, nonce, 0, AesGcmNonceSize);
            Buffer.BlockCopy(combined, AesGcmNonceSize, ciphertext, 0, ctLen);
            Buffer.BlockCopy(combined, AesGcmNonceSize + ctLen, tag, 0, AesGcmTagSize);

            plaintext = new byte[ctLen];
            using var aes = new AesGcm(key, AesGcmTagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
