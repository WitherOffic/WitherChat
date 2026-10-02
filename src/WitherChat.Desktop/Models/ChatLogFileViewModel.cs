using System.Globalization;

namespace WitherChat.Desktop.Models;

public sealed record ChatLogFileViewModel(string Path, string Channel, DateTime Date, long Size)
{
    public string Title => "@" + Channel;
    public string DateText => Date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    public string SizeText => Size < 1024
        ? Size.ToString(CultureInfo.CurrentCulture) + " B"
        : (Size / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
}
