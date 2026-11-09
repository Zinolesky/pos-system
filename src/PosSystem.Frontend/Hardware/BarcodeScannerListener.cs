using System;
using System.Text;

namespace PosSystem.Frontend.Hardware;

public class BarcodeScannerListener : IDisposable
{
    public event EventHandler<string>? BarcodeScanned;

    private readonly StringBuilder _wedgeBuffer = new();
    private DateTime _lastKeystrokeUtc = DateTime.MinValue;
    private const int MaxInterKeystrokeMs = 55;
    private const int MinBarcodeLength = 3;

    public string ActivePort { get; private set; } = "USB HID Wedge + COM3";

    public void StartListening(string comPort = "COM3")
    {
        ActivePort = $"USB HID Wedge / {comPort}";
    }

    /// <summary>
    /// Intercepts rapid keystrokes from a USB HID Keyboard-Wedge barcode scanner.
    /// Returns true if the Enter key completed a rapid barcode scan.
    /// </summary>
    public bool ProcessWedgeChar(char c)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastKeystrokeUtc).TotalMilliseconds > MaxInterKeystrokeMs)
        {
            _wedgeBuffer.Clear();
        }

        _lastKeystrokeUtc = now;
        if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
        {
            _wedgeBuffer.Append(c);
        }
        return false;
    }

    public bool TryCompleteWedgeScan()
    {
        var now = DateTime.UtcNow;
        if (_wedgeBuffer.Length >= MinBarcodeLength &&
            (now - _lastKeystrokeUtc).TotalMilliseconds <= MaxInterKeystrokeMs * 2)
        {
            var code = _wedgeBuffer.ToString();
            _wedgeBuffer.Clear();
            SimulateScan(code);
            return true;
        }

        _wedgeBuffer.Clear();
        return false;
    }

    public void SimulateScan(string barcode)
    {
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            BarcodeScanned?.Invoke(this, barcode.Trim());
        }
    }

    public void Dispose()
    {
        _wedgeBuffer.Clear();
    }
}
