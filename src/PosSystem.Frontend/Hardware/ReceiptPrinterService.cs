using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using PosSystem.Shared.Models;

namespace PosSystem.Frontend.Hardware;

public enum PrinterTransportType
{
    FileMock = 0,
    WindowsDriver = 1,
    NetworkTcp = 2
}

public class ReceiptPrinterService
{
    private const byte ESC = 0x1B;
    private const byte GS = 0x1D;

    public PrinterTransportType TransportType { get; set; } = PrinterTransportType.FileMock;
    public string PrinterTarget { get; set; } = "POS-80"; // Printer Queue Name or "192.168.1.200:9100"

    public string LastRenderedReceiptText { get; private set; } = string.Empty;
    public string LastSavedReceiptPath { get; private set; } = string.Empty;

    public ReceiptPrinterService()
    {
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(settingsPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("Printer", out var printerObj))
                {
                    if (printerObj.TryGetProperty("PrinterType", out var ptProp) &&
                        Enum.TryParse<PrinterTransportType>(ptProp.GetString(), true, out var cfgType))
                    {
                        TransportType = cfgType;
                    }
                    if (printerObj.TryGetProperty("PrinterTarget", out var tgtProp) &&
                        !string.IsNullOrWhiteSpace(tgtProp.GetString()))
                    {
                        PrinterTarget = tgtProp.GetString()!;
                    }
                }
            }
        }
        catch
        {
            // Fallback to defaults if appsettings.json is unreadable
        }

        var envType = Environment.GetEnvironmentVariable("POS_PRINTER_TYPE");
        if (!string.IsNullOrWhiteSpace(envType) && Enum.TryParse<PrinterTransportType>(envType, true, out var parsed))
        {
            TransportType = parsed;
        }

        var envTarget = Environment.GetEnvironmentVariable("POS_PRINTER_TARGET");
        if (!string.IsNullOrWhiteSpace(envTarget))
        {
            PrinterTarget = envTarget;
        }
    }

    public static string GetReceiptsDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosSystem",
            "Receipts");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string FormatReceiptText(Sale sale)
    {
        var sb = new StringBuilder();
        string divider = new string('-', 42);
        string doubleDivider = new string('=', 42);

        sb.AppendLine("            PRIME SUPERMARKET             ");
        sb.AppendLine("       14 Admiralty Way, Lekki Phase 1    ");
        sb.AppendLine("          Tel: +234 800 PRIME POS         ");
        sb.AppendLine(doubleDivider);
        sb.AppendLine($"Receipt #: {sale.ReceiptNumber}");
        sb.AppendLine($"Date:      {sale.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Terminal:  {sale.TerminalId}");
        sb.AppendLine($"Cashier:   {(string.IsNullOrWhiteSpace(sale.CashierName) ? "Staff" : sale.CashierName)}");
        if (!string.IsNullOrWhiteSpace(sale.CustomerName))
        {
            sb.AppendLine($"Customer:  {sale.CustomerName}");
        }
        if (sale.OriginalSaleId.HasValue)
        {
            sb.AppendLine("***   CUSTOMER RETURN / REFUND SLIP   ***");
            if (!string.IsNullOrWhiteSpace(sale.Notes))
                sb.AppendLine($"Ref:       {sale.Notes}");
        }
        else if (sale.Status == SaleStatus.Refunded)
        {
            sb.AppendLine("***        REFUND / RETURN RECEIPT     ***");
        }
        else if (sale.Status == SaleStatus.PartiallyRefunded)
        {
            sb.AppendLine("***     PARTIALLY REFUNDED RECEIPT     ***");
        }
        sb.AppendLine(divider);
        sb.AppendLine($"{"ITEM",-22} {"QTY",4} {"AMOUNT",14}");
        sb.AppendLine(divider);

        foreach (var item in sale.Items)
        {
            string name = item.ProductName.Length > 22 ? item.ProductName[..22] : item.ProductName;
            sb.AppendLine($"{name,-22} {item.Quantity,3}x {item.LineSubtotal,14:N2}");
            if (item.LineDiscountAmount > 0)
            {
                sb.AppendLine($"  (Item Disc: -{item.LineDiscountAmount:N2})");
            }
            if (!sale.OriginalSaleId.HasValue && item.ReturnedQuantity > 0)
            {
                sb.AppendLine($"  (Returned: {item.ReturnedQuantity} of {item.Quantity})");
            }
        }

        sb.AppendLine(divider);
        sb.AppendLine($"{"Subtotal:",-26} NGN {sale.Subtotal,11:N2}");
        if (sale.DiscountAmount > 0)
        {
            string discLabel = sale.DiscountPercentage > 0
                ? $"Discount ({sale.DiscountPercentage:0.#}%):"
                : "Discount:";
            sb.AppendLine($"{discLabel,-26} -NGN {sale.DiscountAmount,10:N2}");
        }
        string vatModeSuffix = sale.IsTaxInclusive ? " Incl" : "";
        sb.AppendLine($"{"VAT (" + (sale.TaxRatePercentage * 100m).ToString("0.#") + "%" + vatModeSuffix + "):",-26} NGN {sale.TaxAmount,11:N2}");
        sb.AppendLine(doubleDivider);
        string totalLabel = sale.OriginalSaleId.HasValue ? "REFUND TOTAL:" : "TOTAL:";
        sb.AppendLine($"{totalLabel,-26} NGN {sale.TotalAmount,11:N2}");
        if (!sale.OriginalSaleId.HasValue && sale.RefundedAmount > 0)
        {
            sb.AppendLine($"{"Refunded Amount:",-26} -NGN {sale.RefundedAmount,10:N2}");
            sb.AppendLine($"{"Net Retained:",-26} NGN {Math.Max(0m, sale.TotalAmount - sale.RefundedAmount),11:N2}");
        }
        sb.AppendLine(doubleDivider);

        sb.AppendLine($"{"Payment Method:",-26} {sale.PaymentMethod,15}");
        if (sale.CashTendered > 0)
            sb.AppendLine($"{"Cash Tendered:",-26} NGN {sale.CashTendered,11:N2}");
        if (sale.CardTendered > 0)
            sb.AppendLine($"{"POS Card Tendered:",-26} NGN {sale.CardTendered,11:N2}");
        if (sale.TransferTendered > 0)
            sb.AppendLine($"{"Bank Transfer:",-26} NGN {sale.TransferTendered,11:N2}");
        if (!string.IsNullOrWhiteSpace(sale.PaymentReference))
            sb.AppendLine($"{"Ref #:",-26} {sale.PaymentReference,15}");
        sb.AppendLine($"{"Change Due:",-26} NGN {sale.ChangeDue,11:N2}");

        sb.AppendLine(divider);
        sb.AppendLine("     Thank you for shopping at Prime!     ");
        sb.AppendLine("   Items returnable within 7 days w/ rcpt ");
        sb.AppendLine($"        * {sale.ReceiptNumber} *         ");
        return sb.ToString();
    }

    public string FormatZReportText(ShiftSession shift)
    {
        var sb = new StringBuilder();
        string divider = new string('-', 42);
        string doubleDivider = new string('=', 42);

        sb.AppendLine("         END-OF-SHIFT Z-REPORT            ");
        sb.AppendLine("            PRIME SUPERMARKET             ");
        sb.AppendLine(doubleDivider);
        sb.AppendLine($"Terminal:    {shift.TerminalId}");
        sb.AppendLine($"Cashier:     {shift.CashierName}");
        sb.AppendLine($"Opened At:   {shift.OpenedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Closed At:   {(shift.ClosedAt ?? DateTime.UtcNow).ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Orders:      {shift.TotalTransactions}");
        sb.AppendLine(divider);
        sb.AppendLine($"{"Opening Float:",-26} NGN {shift.OpeningFloat,11:N2}");
        sb.AppendLine($"{"Cash Sales (Net):",-26} NGN {shift.CashSalesTotal,11:N2}");
        sb.AppendLine($"{"POS Card Sales:",-26} NGN {shift.CardSalesTotal,11:N2}");
        sb.AppendLine($"{"Bank Transfers:",-26} NGN {shift.TransferSalesTotal,11:N2}");
        sb.AppendLine($"{"Refunds Issued:",-26} -NGN {shift.RefundsTotal,10:N2}");
        sb.AppendLine($"{"Safe Drops / Payouts:",-26} -NGN {shift.CashDropsTotal,10:N2}");
        sb.AppendLine(doubleDivider);
        sb.AppendLine($"{"Expected Drawer Cash:",-26} NGN {shift.ExpectedCash,11:N2}");
        sb.AppendLine($"{"Actual Counted Cash:",-26} NGN {(shift.ActualCountedCash ?? 0m),11:N2}");
        sb.AppendLine($"{"Variance (Over/Short):",-26} NGN {(shift.Variance ?? 0m),11:N2}");
        sb.AppendLine(doubleDivider);
        return sb.ToString();
    }

    public byte[] BuildReceipt(Sale sale, bool kickDrawer = true)
    {
        return BuildEscPosPayload(FormatReceiptText(sale), kickDrawer);
    }

    public byte[] BuildEscPosPayload(string text, bool kickDrawer)
    {
        var builder = new StringBuilder();
        builder.Append((char)ESC).Append('@'); // Initialize printer
        builder.Append(text);
        builder.Append("\n\n\n");

        var textBytes = Encoding.UTF8.GetBytes(builder.ToString());
        var cutCommand = new byte[] { GS, (byte)'V', 66, 0 };
        var drawerKickCommand = kickDrawer
            ? new byte[] { ESC, (byte)'p', 0, 25, 250 }
            : Array.Empty<byte>();

        var finalPayload = new byte[textBytes.Length + cutCommand.Length + drawerKickCommand.Length];
        Buffer.BlockCopy(textBytes, 0, finalPayload, 0, textBytes.Length);
        Buffer.BlockCopy(cutCommand, 0, finalPayload, textBytes.Length, cutCommand.Length);
        if (drawerKickCommand.Length > 0)
        {
            Buffer.BlockCopy(drawerKickCommand, 0, finalPayload, textBytes.Length + cutCommand.Length, drawerKickCommand.Length);
        }

        return finalPayload;
    }

    public string PrintReceipt(Sale sale)
    {
        bool shouldKickDrawer = sale.PaymentMethod == PaymentMethod.Cash || sale.CashTendered > 0;
        byte[] payload = BuildReceipt(sale, shouldKickDrawer);

        LastRenderedReceiptText = FormatReceiptText(sale);
        string fileName = $"{sale.ReceiptNumber}_{DateTime.Now:HHmmss}.txt";
        LastSavedReceiptPath = Path.Combine(GetReceiptsDirectory(), fileName);
        File.WriteAllText(LastSavedReceiptPath, LastRenderedReceiptText, Encoding.UTF8);

        DispatchRawEscPosPayload(payload, $"PrimePOS Receipt {sale.ReceiptNumber}");
        return LastRenderedReceiptText;
    }

    public string PrintZReport(ShiftSession shift)
    {
        string text = FormatZReportText(shift);
        LastRenderedReceiptText = text;
        string fileName = $"ZREPORT_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        LastSavedReceiptPath = Path.Combine(GetReceiptsDirectory(), fileName);
        File.WriteAllText(LastSavedReceiptPath, text, Encoding.UTF8);

        byte[] payload = BuildEscPosPayload(text, kickDrawer: true);
        DispatchRawEscPosPayload(payload, $"PrimePOS Z-Report {shift.Id}");
        return text;
    }

    private void DispatchRawEscPosPayload(byte[] payload, string documentName)
    {
        try
        {
            if (TransportType == PrinterTransportType.WindowsDriver && !string.IsNullOrWhiteSpace(PrinterTarget))
            {
                RawWindowsPrinterSpooler.SendBytesToPrinter(PrinterTarget, payload, documentName);
            }
            else if (TransportType == PrinterTransportType.NetworkTcp && !string.IsNullOrWhiteSpace(PrinterTarget))
            {
                var parts = PrinterTarget.Split(':');
                string host = parts[0];
                int port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 9100;

                using var client = new TcpClient();
                if (client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)))
                {
                    using var stream = client.GetStream();
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
            }
        }
        catch
        {
            // Fallback archive in %LocalAppData%\PosSystem\Receipts is always preserved
        }
    }
}

public static class RawWindowsPrinterSpooler
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class DOCINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pDocName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pDataType;
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPWStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFO di);

    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static bool SendBytesToPrinter(string printerName, byte[] bytes, string docName = "PrimePOS Receipt")
    {
        if (!OpenPrinter(printerName.Normalize(), out IntPtr hPrinter, IntPtr.Zero))
            return false;

        try
        {
            var di = new DOCINFO
            {
                pDocName = docName,
                pDataType = "RAW"
            };

            if (!StartDocPrinter(hPrinter, 1, di))
                return false;

            try
            {
                if (!StartPagePrinter(hPrinter))
                    return false;

                IntPtr unmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                    return WritePrinter(hPrinter, unmanagedBytes, bytes.Length, out _);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(unmanagedBytes);
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }
}
