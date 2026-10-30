using System.Runtime.InteropServices;
using System.Security;
using System.Windows;

namespace OhMySsh.Desktop.Services;

/// <summary>Reads native file drops without accepting text, links, or serialized objects.</summary>
internal static class TransferDropData
{
    public static string[] ReadFiles(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
                && data.GetData(DataFormats.FileDrop, autoConvert: false) is string[] paths
                ? paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() : [];
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or SecurityException)
        {
            return [];
        }
    }
}
