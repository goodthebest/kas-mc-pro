namespace Miningcore.Util;

public static class FormatUtil
{
    public static readonly string[] HashrateUnits = { " H/s", " KH/s", " MH/s", " GH/s", " TH/s", " PH/s" , " EH/s" };
    public static readonly string[] QuantityUnits = { "", "K", "M", "G", "T", "P", "E", "Z", "Y" };
    public static readonly string[] CapacityUnits = { " KB", " MB", " GB", " TB", " PB" };

    public static string FormatHashrate(double hashrate)
    {
        var hashrateUnits = HashrateUnits;

        var i = 0;

        while (hashrate > 1024 && i < hashrateUnits.Length - 1)
        {
            hashrate /= 1024;
            i++;
        }

        return Math.Round(hashrate, 2).ToString("F2") + hashrateUnits[i];
    }

    public static string FormatCapacity(double hashrate)
    {
        var i = -1;

        do
        {
            hashrate /= 1024;
            i++;
        } while(hashrate > 1024 && i < CapacityUnits.Length - 1);

        return (int) Math.Abs(hashrate) + CapacityUnits[i];
    }

    public static string FormatQuantity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return "0";

        var i = 0; // Start at 0 since we now have "" as first unit

        while (value >= 1000 && i < QuantityUnits.Length - 1)
        {
            value /= 1000;
            i++;
        }

        // Ensure index is within bounds
        i = Math.Min(i, QuantityUnits.Length - 1);
        i = Math.Max(i, 0);

        return Math.Round(value, 2) + QuantityUnits[i];
    }
}
