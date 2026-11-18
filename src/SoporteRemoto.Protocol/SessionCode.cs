using System.Security.Cryptography;

namespace SoporteRemoto.Protocol;

/// <summary>Código de sesión de 9 caracteres, sin caracteres ambiguos (I, O, 0, 1).</summary>
public static class SessionCode
{
    public const int Length = 9;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var buffer = new char[Length];
        for (var i = 0; i < Length; i++)
            buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(buffer);
    }

    /// <summary>Quita separadores y valida; devuelve "" si el código no es válido.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var chars = new char[Length];
        var count = 0;
        foreach (var c in raw)
        {
            if (c is ' ' or '-' or '_') continue;
            if (!char.IsLetterOrDigit(c)) return "";
            var upper = char.ToUpperInvariant(c);
            if (Alphabet.IndexOf(upper) < 0) return "";
            if (count == Length) return "";
            chars[count++] = upper;
        }
        return count == Length ? new string(chars) : "";
    }

    public static bool IsValid(string? code) => Normalize(code).Length == Length;

    /// <summary>Indica si un carácter pertenece al alfabeto del código (sin ambiguos).</summary>
    public static bool IsAllowedChar(char c) => Alphabet.IndexOf(char.ToUpperInvariant(c)) >= 0;

    public static string Format(string code) =>
        code.Length == Length ? $"{code[..3]}-{code[3..6]}-{code[6..]}" : code;
}
