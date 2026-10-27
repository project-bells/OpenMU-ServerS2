// <copyright file="Windows874Encoding.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Network.Packets;

using System.Text;

/// <summary>
/// Windows-874 compatible encoding for classic MU Online identity fields.
/// The original protocol reserves ten bytes for names. UTF-8 would therefore
/// truncate Thai text in the middle of a character; Windows-874 keeps one Thai
/// code point per byte while retaining ASCII compatibility.
/// </summary>
public sealed class Windows874Encoding : Encoding
{
    private Windows874Encoding()
    {
    }

    /// <summary>
    /// Gets the shared instance.
    /// </summary>
    public static Windows874Encoding Instance { get; } = new();

    /// <summary>
    /// Determines whether every character can be represented losslessly.
    /// </summary>
    public static bool IsRepresentable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.All(IsRepresentable);
    }

    /// <inheritdoc />
    public override int GetByteCount(char[] chars, int index, int count) => count;

    /// <inheritdoc />
    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        for (int i = 0; i < charCount; i++)
        {
            bytes[byteIndex + i] = Encode(chars[charIndex + i]);
        }

        return charCount;
    }

    /// <inheritdoc />
    public override int GetCharCount(byte[] bytes, int index, int count) => count;

    /// <inheritdoc />
    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        for (int i = 0; i < byteCount; i++)
        {
            chars[charIndex + i] = Decode(bytes[byteIndex + i]);
        }

        return byteCount;
    }

    /// <inheritdoc />
    public override int GetMaxByteCount(int charCount) => charCount;

    /// <inheritdoc />
    public override int GetMaxCharCount(int byteCount) => byteCount;

    private static byte Encode(char value) => value switch
    {
        <= '\u007F' => (byte)value,
        >= '\u0E01' and <= '\u0E3A' => (byte)(0xA1 + value - '\u0E01'),
        '\u0E3F' => 0xDF,
        >= '\u0E40' and <= '\u0E5B' => (byte)(0xE0 + value - '\u0E40'),
        _ => (byte)'?',
    };

    private static bool IsRepresentable(char value) => value is <= '\u007F'
        or >= '\u0E01' and <= '\u0E3A'
        or '\u0E3F'
        or >= '\u0E40' and <= '\u0E5B';

    private static char Decode(byte value) => value switch
    {
        <= 0x7F => (char)value,
        >= 0xA1 and <= 0xDA => (char)('\u0E01' + value - 0xA1),
        0xDF => '\u0E3F',
        >= 0xE0 and <= 0xFB => (char)('\u0E40' + value - 0xE0),
        _ => '\uFFFD',
    };
}
