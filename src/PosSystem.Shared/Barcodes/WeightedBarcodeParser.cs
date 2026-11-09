using System;
using System.Collections.Generic;
using System.Linq;
using PosSystem.Shared.Models;

namespace PosSystem.Shared.Barcodes;

public enum EmbeddedBarcodeKind
{
    WeightGrams = 0,
    PriceWholeCurrency = 1,
    PriceSubunitCurrency = 2
}

public record WeightedBarcodeMatch(
    Product Product,
    EmbeddedBarcodeKind Kind,
    string ItemPlu,
    decimal WeightKg,
    decimal CalculatedUnitPrice,
    decimal CalculatedCostPrice,
    string DisplayLabel);

public static class WeightedBarcodeParser
{
    /// <summary>
    /// Returns true if the 13-digit string starts with a GS1 variable-measure retail prefix (02 or 20..29).
    /// </summary>
    public static bool IsCandidateVariableMeasureEan13(string? rawBarcode)
    {
        if (string.IsNullOrWhiteSpace(rawBarcode)) return false;
        var code = rawBarcode.Trim();
        if (code.Length != 13 || !code.All(char.IsDigit)) return false;

        string prefix = code[..2];
        if (prefix == "02") return true;
        return int.TryParse(prefix, out int p) && p >= 20 && p <= 29;
    }

    public static int ComputeEan13CheckDigit(ReadOnlySpan<char> first12Digits)
    {
        if (first12Digits.Length != 12)
            throw new ArgumentException("EAN-13 check digit calculation requires 12 digits.", nameof(first12Digits));

        int sum = 0;
        for (int i = 0; i < 12; i++)
        {
            if (!char.IsDigit(first12Digits[i]))
                throw new ArgumentException("EAN-13 payload must contain only digits.", nameof(first12Digits));

            int digit = first12Digits[i] - '0';
            sum += (i % 2 == 0) ? digit : digit * 3;
        }

        return (10 - (sum % 10)) % 10;
    }

    public static bool IsValidEan13(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return false;
        var code = barcode.Trim();
        if (code.Length != 13 || !code.All(char.IsDigit)) return false;

        int expected = ComputeEan13CheckDigit(code.AsSpan(0, 12));
        int actual = code[12] - '0';
        return expected == actual;
    }

    public static string BuildWeightEmbeddedBarcode(string itemPlu, int weightGrams, string prefix = "20")
    {
        if (prefix.Length != 2 || !prefix.All(char.IsDigit))
            throw new ArgumentException("Prefix must be 2 digits (e.g. '20').", nameof(prefix));
        if (weightGrams <= 0 || weightGrams > 99999)
            throw new ArgumentOutOfRangeException(nameof(weightGrams), "Weight in grams must be between 1 and 99999.");

        string digitsOnlyPlu = new string(itemPlu.Where(char.IsDigit).ToArray());
        string plu5 = digitsOnlyPlu.Length >= 5
            ? digitsOnlyPlu[^5..]
            : digitsOnlyPlu.PadLeft(5, '0');

        string grams5 = weightGrams.ToString("D5");
        string payload12 = $"{prefix}{plu5}{grams5}";
        int check = ComputeEan13CheckDigit(payload12);
        return $"{payload12}{check}";
    }

    public static string BuildPriceEmbeddedBarcode(string itemPlu, decimal totalPrice, string prefix = "28")
    {
        if (prefix.Length != 2 || !prefix.All(char.IsDigit))
            throw new ArgumentException("Prefix must be 2 digits (e.g. '28' or '29').", nameof(prefix));

        string digitsOnlyPlu = new string(itemPlu.Where(char.IsDigit).ToArray());
        string plu5 = digitsOnlyPlu.Length >= 5
            ? digitsOnlyPlu[^5..]
            : digitsOnlyPlu.PadLeft(5, '0');

        int encodedValue = prefix == "29"
            ? (int)Math.Round(totalPrice * 100m, 0, MidpointRounding.AwayFromZero)
            : (int)Math.Round(totalPrice, 0, MidpointRounding.AwayFromZero);

        if (encodedValue <= 0 || encodedValue > 99999)
            throw new ArgumentOutOfRangeException(nameof(totalPrice), "Encoded price value must fit within 5 digits (1..99999).");

        string price5 = encodedValue.ToString("D5");
        string payload12 = $"{prefix}{plu5}{price5}";
        int check = ComputeEan13CheckDigit(payload12);
        return $"{payload12}{check}";
    }

    /// <summary>
    /// Attempts to parse a 13-digit deli/produce variable-measure EAN-13 barcode:
    /// - Prefixes 20..24: PP + IIIII (5-digit PLU) + WWWWW (weight in grams, e.g. 00450 = 0.450 kg) + C
    /// - Prefixes 02, 25..28: PP + IIIII (5-digit PLU) + PPPPP (price in whole currency units) + C
    /// - Prefix 29: 29 + IIIII (5-digit PLU) + PPPPP (price in minor subunits / 100) + C
    /// </summary>
    public static bool TryParse(
        string? rawBarcode,
        IEnumerable<Product> catalog,
        out WeightedBarcodeMatch? match,
        out string? errorMessage)
    {
        match = null;
        errorMessage = null;

        if (!IsCandidateVariableMeasureEan13(rawBarcode))
            return false;

        string code = rawBarcode!.Trim();
        if (!IsValidEan13(code))
        {
            errorMessage = $"Invalid EAN-13 check digit on variable-measure label '{code}'.";
            return false;
        }

        string prefix = code.Substring(0, 2);
        string pluRaw = code.Substring(2, 5);
        string valueRaw = code.Substring(7, 5);

        if (!int.TryParse(valueRaw, out int encodedValue) || encodedValue <= 0)
        {
            errorMessage = $"Zero or invalid embedded weight/price in barcode '{code}'.";
            return false;
        }

        var product = ResolveProductByPlu(pluRaw, catalog);
        if (product == null)
        {
            errorMessage = $"Deli/Produce PLU '{pluRaw}' from label '{code}' was not found in catalog.";
            return false;
        }

        int prefixNum = int.Parse(prefix);
        if (prefixNum >= 20 && prefixNum <= 24)
        {
            decimal weightKg = Math.Round(encodedValue / 1000m, 3, MidpointRounding.AwayFromZero);
            decimal packPrice = Math.Round(product.Price * weightKg, 2, MidpointRounding.AwayFromZero);
            decimal packCost = Math.Round(product.CostPrice * weightKg, 2, MidpointRounding.AwayFromZero);
            string unitLabel = string.IsNullOrWhiteSpace(product.Unit) ? "kg" : product.Unit;

            match = new WeightedBarcodeMatch(
                product,
                EmbeddedBarcodeKind.WeightGrams,
                pluRaw,
                weightKg,
                packPrice,
                packCost,
                $"{product.Name} ({weightKg:0.000} {unitLabel})");
            return true;
        }
        else
        {
            bool isSubunit = prefix == "29";
            decimal packPrice = isSubunit
                ? Math.Round(encodedValue / 100m, 2, MidpointRounding.AwayFromZero)
                : encodedValue;

            decimal impliedWeightKg = product.Price > 0m
                ? Math.Round(packPrice / product.Price, 3, MidpointRounding.AwayFromZero)
                : 1m;
            decimal packCost = Math.Round(product.CostPrice * impliedWeightKg, 2, MidpointRounding.AwayFromZero);
            string unitLabel = string.IsNullOrWhiteSpace(product.Unit) ? "kg" : product.Unit;

            match = new WeightedBarcodeMatch(
                product,
                isSubunit ? EmbeddedBarcodeKind.PriceSubunitCurrency : EmbeddedBarcodeKind.PriceWholeCurrency,
                pluRaw,
                impliedWeightKg,
                packPrice,
                packCost,
                $"{product.Name} ({impliedWeightKg:0.000} {unitLabel})");
            return true;
        }
    }

    private static Product? ResolveProductByPlu(string plu5, IEnumerable<Product> catalog)
    {
        string trimmedPlu = plu5.TrimStart('0');
        if (string.IsNullOrEmpty(trimmedPlu)) trimmedPlu = "0";

        foreach (var p in catalog)
        {
            if (!string.IsNullOrWhiteSpace(p.Barcode))
            {
                string bcTrimmed = p.Barcode.Trim().TrimStart('0');
                if (string.Equals(p.Barcode.Trim(), plu5, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bcTrimmed, trimmedPlu, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }

            if (!string.IsNullOrWhiteSpace(p.Sku))
            {
                string skuDigits = new string(p.Sku.Where(char.IsDigit).ToArray()).TrimStart('0');
                if (!string.IsNullOrEmpty(skuDigits) &&
                    (string.Equals(skuDigits, trimmedPlu, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(p.Sku.Trim(), plu5, StringComparison.OrdinalIgnoreCase)))
                {
                    return p;
                }
            }
        }

        return null;
    }
}
