using System.Globalization;
using System.Numerics;

namespace EtlTool.Application.Transformations;

internal readonly record struct ExactNumericText(BigInteger Significand, int Scale)
{
    private static readonly BigInteger DecimalMaximumSignificand =
        BigInteger.Parse("79228162514264337593543950335", CultureInfo.InvariantCulture);

    public bool IsMathematicallyIntegral => Scale == 0;

    public static ExactNumericText Parse(string text, CultureInfo sourceCulture)
    {
        _ = decimal.Parse(text, NumberStyles.Number, sourceCulture);

        var numberFormat = sourceCulture.NumberFormat;
        var normalized = text.Trim();
        var isNegative = ConsumeSign(ref normalized, numberFormat);
        normalized = normalized.Trim();

        if (!string.IsNullOrEmpty(numberFormat.NumberGroupSeparator))
        {
            normalized = normalized.Replace(
                numberFormat.NumberGroupSeparator,
                string.Empty,
                StringComparison.Ordinal);
        }

        var decimalSeparator = numberFormat.NumberDecimalSeparator;
        var decimalIndex = string.IsNullOrEmpty(decimalSeparator)
            ? -1
            : normalized.IndexOf(decimalSeparator, StringComparison.Ordinal);
        var integerDigits = decimalIndex < 0
            ? normalized
            : normalized[..decimalIndex];
        var fractionalDigits = decimalIndex < 0
            ? string.Empty
            : normalized[(decimalIndex + decimalSeparator.Length)..];
        var digits = string.Concat(
            integerDigits.Length == 0 ? "0" : integerDigits,
            fractionalDigits);
        var significand = BigInteger.Parse(
            digits,
            NumberStyles.None,
            CultureInfo.InvariantCulture);

        if (isNegative && !significand.IsZero)
        {
            significand = BigInteger.Negate(significand);
        }

        var scale = fractionalDigits.Length;
        while (scale > 0 && significand % 10 == 0)
        {
            significand /= 10;
            scale--;
        }

        return new ExactNumericText(significand, scale);
    }

    public decimal ToDecimal()
    {
        var magnitude = BigInteger.Abs(Significand);
        if (Scale > 28 || magnitude > DecimalMaximumSignificand)
        {
            throw new OverflowException("The number cannot be represented exactly as a Decimal value.");
        }

        var low = (uint)(magnitude & uint.MaxValue);
        var middle = (uint)((magnitude >> 32) & uint.MaxValue);
        var high = (uint)((magnitude >> 64) & uint.MaxValue);

        return new decimal(
            unchecked((int)low),
            unchecked((int)middle),
            unchecked((int)high),
            Significand.Sign < 0,
            (byte)Scale);
    }

    public long ToInt64()
    {
        if (!IsMathematicallyIntegral
            || Significand < long.MinValue
            || Significand > long.MaxValue)
        {
            throw new OverflowException("The number cannot be represented exactly as an Int64 value.");
        }

        return (long)Significand;
    }

    private static bool ConsumeSign(
        ref string text,
        NumberFormatInfo numberFormat)
    {
        if (TryConsumeSign(ref text, numberFormat.NegativeSign))
        {
            return true;
        }

        _ = TryConsumeSign(ref text, numberFormat.PositiveSign);
        return false;
    }

    private static bool TryConsumeSign(ref string text, string sign)
    {
        if (string.IsNullOrEmpty(sign))
        {
            return false;
        }

        if (text.StartsWith(sign, StringComparison.Ordinal))
        {
            text = text[sign.Length..];
            return true;
        }

        if (text.EndsWith(sign, StringComparison.Ordinal))
        {
            text = text[..^sign.Length];
            return true;
        }

        return false;
    }
}
