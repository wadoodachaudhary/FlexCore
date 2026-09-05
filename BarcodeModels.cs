namespace Fx.ControlKit;

/// <summary>Supported, scannable one-dimensional barcode symbologies.</summary>
public enum BarcodeType
{
    Code128, Code39, Code93, Ean8, Ean13, UpcA, UpcE, Itf, Codabar
}

/// <summary>QR error correction levels, from lowest to highest redundancy.</summary>
public enum QrErrorCorrectionLevel { Low, Medium, Quartile, High }
