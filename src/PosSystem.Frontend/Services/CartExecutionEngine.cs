using System;
using System.Collections.Generic;
using System.Linq;
using PosSystem.Shared.Barcodes;
using PosSystem.Shared.Models;

namespace PosSystem.Frontend.Services;

public record PartialRefundQuote(
    decimal RefundSubtotal,
    decimal RefundDiscount,
    decimal RefundTax,
    decimal RefundTotal,
    bool IsFullRemainingReturn,
    List<SaleLineItem> ReturnedItems);

public class CartExecutionEngine
{
    public const decimal LoyaltyPointValueNaira = 10m; // 1 loyalty point = ₦10.00 redemption value

    public void RecalculateCart(Sale currentSale, TaxRate activeTaxRate)
    {
        currentSale.TaxRatePercentage = activeTaxRate.Percentage;
        currentSale.IsTaxInclusive = activeTaxRate.IsInclusive;

        decimal rawSubtotal = 0m;

        foreach (var item in currentSale.Items)
        {
            if (item.Quantity < 0) item.Quantity = 0;

            // Automatic Case-Pack / Wholesale Tiered Pricing (unless manually overridden or weighed label)
            if (!item.MeasuredWeightKg.HasValue && !item.IsManualPriceOverride && item.WholesaleMinQty > 0 && item.WholesaleUnitPrice > 0m)
            {
                if (item.Quantity >= item.WholesaleMinQty)
                {
                    item.UnitPrice = item.WholesaleUnitPrice;
                }
                else if (item.StandardRetailPrice > 0m)
                {
                    item.UnitPrice = item.StandardRetailPrice;
                }
            }

            var grossLine = Math.Round(item.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);
            var clampedLineDiscount = Math.Clamp(item.LineDiscountAmount, 0m, grossLine);
            item.LineDiscountAmount = clampedLineDiscount;
            item.LineSubtotal = grossLine - clampedLineDiscount;

            rawSubtotal += item.LineSubtotal;
        }

        currentSale.Subtotal = Math.Round(rawSubtotal, 2, MidpointRounding.AwayFromZero);

        decimal loyaltyDiscount = Math.Max(0m, currentSale.LoyaltyPointsRedeemed * LoyaltyPointValueNaira);
        if (currentSale.DiscountPercentage > 0m)
        {
            var pct = Math.Clamp(currentSale.DiscountPercentage, 0m, 100m);
            currentSale.DiscountPercentage = pct;
            decimal pctDisc = Math.Round(currentSale.Subtotal * (pct / 100m), 2, MidpointRounding.AwayFromZero);
            currentSale.DiscountAmount = Math.Clamp(pctDisc + loyaltyDiscount, 0m, currentSale.Subtotal);
        }
        else if (currentSale.LoyaltyPointsRedeemed > 0)
        {
            currentSale.DiscountAmount = Math.Clamp(loyaltyDiscount, 0m, currentSale.Subtotal);
        }
        else
        {
            currentSale.DiscountAmount = Math.Clamp(currentSale.DiscountAmount, 0m, currentSale.Subtotal);
        }

        decimal discountFactor = currentSale.Subtotal > 0m
            ? Math.Max(0m, (currentSale.Subtotal - currentSale.DiscountAmount) / currentSale.Subtotal)
            : 1m;

        decimal totalTax = 0m;
        foreach (var item in currentSale.Items)
        {
            decimal discountedLineNet = Math.Round(item.LineSubtotal * discountFactor, 2, MidpointRounding.AwayFromZero);

            if (item.IsTaxable && activeTaxRate.Percentage > 0m)
            {
                if (activeTaxRate.IsInclusive)
                {
                    item.LineTax = Math.Round(
                        discountedLineNet - (discountedLineNet / (1m + activeTaxRate.Percentage)),
                        2,
                        MidpointRounding.AwayFromZero);
                    item.LineTotal = item.LineSubtotal;
                }
                else
                {
                    item.LineTax = Math.Round(
                        discountedLineNet * activeTaxRate.Percentage,
                        2,
                        MidpointRounding.AwayFromZero);
                    item.LineTotal = item.LineSubtotal + item.LineTax;
                }
            }
            else
            {
                item.LineTax = 0m;
                item.LineTotal = item.LineSubtotal;
            }

            totalTax += item.LineTax;
        }

        currentSale.TaxAmount = Math.Round(totalTax, 2, MidpointRounding.AwayFromZero);

        var netAfterDiscount = Math.Max(0m, currentSale.Subtotal - currentSale.DiscountAmount);
        currentSale.TotalAmount = activeTaxRate.IsInclusive
            ? Math.Round(netAfterDiscount, 2, MidpointRounding.AwayFromZero)
            : Math.Round(netAfterDiscount + currentSale.TaxAmount, 2, MidpointRounding.AwayFromZero);

        decimal nonCashTotal = currentSale.CardTendered + currentSale.TransferTendered + currentSale.StoreCreditTendered;
        currentSale.AmountTendered = currentSale.CashTendered + nonCashTotal;

        // Change due can only be dispensed in cash against the cash balance remaining after non-cash tenders
        decimal nonCashApplied = Math.Min(currentSale.TotalAmount, nonCashTotal);
        decimal cashBalanceDue = Math.Max(0m, currentSale.TotalAmount - nonCashApplied);
        currentSale.ChangeDue = Math.Max(0m, Math.Round(currentSale.CashTendered - cashBalanceDue, 2, MidpointRounding.AwayFromZero));
    }

    public void AddItemToCart(Sale currentSale, Product product, int quantity, TaxRate activeTaxRate, bool negativeStockAuthorized = false)
    {
        if (quantity <= 0) quantity = 1;

        var existingItem = currentSale.Items.FirstOrDefault(i =>
            i.ProductId == product.Id &&
            !i.IsManualPriceOverride &&
            !i.MeasuredWeightKg.HasValue);

        if (existingItem != null)
        {
            existingItem.Quantity += quantity;
            existingItem.StandardRetailPrice = product.Price;
            existingItem.WholesaleMinQty = product.WholesaleMinQty;
            existingItem.WholesaleUnitPrice = product.WholesaleUnitPrice;
            if (negativeStockAuthorized)
                existingItem.IsNegativeStockAuthorized = true;
        }
        else
        {
            currentSale.Items.Add(new SaleLineItem
            {
                Id = Ulid.NewUlid(),
                SaleId = currentSale.Id,
                ProductId = product.Id,
                ProductName = product.Name,
                Sku = product.Sku,
                Quantity = quantity,
                UnitPrice = product.Price,
                StandardRetailPrice = product.Price,
                WholesaleMinQty = product.WholesaleMinQty,
                WholesaleUnitPrice = product.WholesaleUnitPrice,
                CostPrice = product.CostPrice,
                IsTaxable = product.IsTaxable,
                IsNegativeStockAuthorized = negativeStockAuthorized
            });
        }

        RecalculateCart(currentSale, activeTaxRate);
    }

    public void AddWeightedItemToCart(Sale currentSale, WeightedBarcodeMatch match, TaxRate activeTaxRate, bool negativeStockAuthorized = false)
    {
        currentSale.Items.Add(new SaleLineItem
        {
            Id = Ulid.NewUlid(),
            SaleId = currentSale.Id,
            ProductId = match.Product.Id,
            ProductName = match.DisplayLabel,
            Sku = match.Product.Sku,
            Quantity = 1,
            MeasuredWeightKg = match.WeightKg,
            UnitPrice = match.CalculatedUnitPrice,
            StandardRetailPrice = match.CalculatedUnitPrice,
            CostPrice = match.CalculatedCostPrice,
            IsTaxable = match.Product.IsTaxable,
            IsNegativeStockAuthorized = negativeStockAuthorized
        });

        RecalculateCart(currentSale, activeTaxRate);
    }

    public void UpdateItemQuantity(Sale currentSale, SaleLineItem item, int newQuantity, TaxRate activeTaxRate)
    {
        if (newQuantity <= 0)
        {
            currentSale.Items.Remove(item);
        }
        else
        {
            item.Quantity = newQuantity;
        }

        RecalculateCart(currentSale, activeTaxRate);
    }

    public void ApplyItemPriceOverride(Sale currentSale, SaleLineItem item, decimal newUnitPrice, TaxRate activeTaxRate)
    {
        if (newUnitPrice < 0m) newUnitPrice = 0m;
        item.IsManualPriceOverride = true;
        item.UnitPrice = Math.Round(newUnitPrice, 2, MidpointRounding.AwayFromZero);
        RecalculateCart(currentSale, activeTaxRate);
    }

    public void ApplyCartDiscountPercentage(Sale currentSale, decimal discountPercent, TaxRate activeTaxRate)
    {
        currentSale.DiscountPercentage = Math.Clamp(discountPercent, 0m, 100m);
        RecalculateCart(currentSale, activeTaxRate);
    }

    public PartialRefundQuote CalculatePartialRefund(
        Sale originalSale,
        IReadOnlyDictionary<Ulid, int> lineReturnQuantities)
    {
        var returnableLines = originalSale.Items
            .Where(i => i.RemainingReturnableQuantity > 0 && i.Quantity > 0)
            .ToList();

        if (!returnableLines.Any())
            return new PartialRefundQuote(0m, 0m, 0m, 0m, false, new List<SaleLineItem>());

        decimal cartDiscFactor = originalSale.Subtotal > 0m
            ? Math.Max(0m, (originalSale.Subtotal - originalSale.DiscountAmount) / originalSale.Subtotal)
            : 1m;

        decimal refundSubtotal = 0m;
        decimal refundDiscount = 0m;
        decimal refundTax = 0m;
        decimal refundTotal = 0m;
        var returnedItems = new List<SaleLineItem>();

        bool isFullRemaining = true;

        foreach (var line in returnableLines)
        {
            int requested = lineReturnQuantities.TryGetValue(line.Id, out var qty) ? qty : 0;
            int clamped = Math.Clamp(requested, 0, line.RemainingReturnableQuantity);

            if (clamped < line.RemainingReturnableQuantity)
                isFullRemaining = false;

            if (clamped <= 0)
                continue;

            decimal ratio = (decimal)clamped / line.Quantity;
            decimal partLineSubtotal = Math.Round(line.LineSubtotal * ratio, 2, MidpointRounding.AwayFromZero);
            decimal partLineDiscount = Math.Round(line.LineDiscountAmount * ratio, 2, MidpointRounding.AwayFromZero);
            decimal discountedNet = Math.Round(partLineSubtotal * cartDiscFactor, 2, MidpointRounding.AwayFromZero);
            decimal cartDiscShare = Math.Max(0m, partLineSubtotal - discountedNet);

            decimal partTax = 0m;
            decimal partTotal = discountedNet;

            if (line.IsTaxable && originalSale.TaxRatePercentage > 0m)
            {
                if (originalSale.IsTaxInclusive)
                {
                    partTax = Math.Round(
                        discountedNet - (discountedNet / (1m + originalSale.TaxRatePercentage)),
                        2,
                        MidpointRounding.AwayFromZero);
                    partTotal = discountedNet;
                }
                else
                {
                    partTax = Math.Round(
                        discountedNet * originalSale.TaxRatePercentage,
                        2,
                        MidpointRounding.AwayFromZero);
                    partTotal = discountedNet + partTax;
                }
            }

            refundSubtotal += partLineSubtotal;
            refundDiscount += cartDiscShare;
            refundTax += partTax;
            refundTotal += partTotal;

            returnedItems.Add(new SaleLineItem
            {
                Id = Ulid.NewUlid(),
                ProductId = line.ProductId,
                ProductName = line.ProductName,
                Sku = line.Sku,
                Quantity = clamped,
                ReturnedQuantity = clamped,
                MeasuredWeightKg = line.MeasuredWeightKg,
                UnitPrice = line.UnitPrice,
                CostPrice = line.CostPrice,
                LineDiscountAmount = partLineDiscount,
                IsTaxable = line.IsTaxable,
                LineSubtotal = partLineSubtotal,
                LineTax = partTax,
                LineTotal = partTotal
            });
        }

        if (!returnedItems.Any())
            return new PartialRefundQuote(0m, 0m, 0m, 0m, false, returnedItems);

        refundSubtotal = Math.Round(refundSubtotal, 2, MidpointRounding.AwayFromZero);
        refundDiscount = Math.Round(refundDiscount, 2, MidpointRounding.AwayFromZero);
        refundTax = Math.Round(refundTax, 2, MidpointRounding.AwayFromZero);
        refundTotal = Math.Round(refundTotal, 2, MidpointRounding.AwayFromZero);

        // Snap residual kobo when returning all remaining items so cumulative refunds match originalSale.TotalAmount
        if (isFullRemaining)
        {
            decimal remainingUnrefundedBalance = Math.Max(0m, Math.Round(originalSale.TotalAmount - originalSale.RefundedAmount, 2, MidpointRounding.AwayFromZero));
            if (originalSale.RefundedAmount == 0m || Math.Abs(remainingUnrefundedBalance - refundTotal) <= 0.05m)
            {
                refundTotal = remainingUnrefundedBalance;
            }
        }

        return new PartialRefundQuote(
            refundSubtotal,
            refundDiscount,
            refundTax,
            refundTotal,
            isFullRemaining,
            returnedItems);
    }
}
