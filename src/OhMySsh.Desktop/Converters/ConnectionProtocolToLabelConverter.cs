using System.Globalization;
using System.Windows.Data;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.Converters;

public sealed class ConnectionProtocolToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ConnectionProtocol.Ssh => "SSH / SFTP",
        ConnectionProtocol.Sftp => "SFTP",
        ConnectionProtocol.Ftp => "FTP",
        ConnectionProtocol.Ftps => "FTPS",
        _ => "Conexão"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
