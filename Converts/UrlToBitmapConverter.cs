using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SMCL.Converters
{
    public class UrlToBitmapConverter : IValueConverter
    {
        private static readonly HttpClient Client = new HttpClient();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    var stream = Client.GetStreamAsync(url).GetAwaiter().GetResult();
                    return new Bitmap(stream);
                }
                catch { return null; }
            }
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}